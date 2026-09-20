using IsotopeProbe.Domain;
using IsotopeProbe.Identity;
using IsotopeProbe.Nuclei;
using IsotopeProbe.Persistence;
using IsotopeProbe.Queries;
using IsotopeProbe.Queue;
using IsotopeProbe.Targets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace IsotopeProbe.Tests;

public sealed class TargetTests : IAsyncLifetime
{
    private readonly string schema = "target_test_" + Guid.NewGuid().ToString("N");
    private NpgsqlConnection? admin;
    private string connection = "";
    private IsotopeProbeDbContext Context() => new(new DbContextOptionsBuilder<IsotopeProbeDbContext>().UseNpgsql(connection).Options);
    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ISOTOPEPROBE_TEST_CONNECTION_STRING");
        if (string.IsNullOrEmpty(configured)) return;
        admin = new(configured);
        await admin.OpenAsync();
        await new NpgsqlCommand($"CREATE SCHEMA {schema}", admin).ExecuteNonQueryAsync();
        connection = new NpgsqlConnectionStringBuilder(configured) { SearchPath = schema, Pooling = false }.ConnectionString;
    }
    public async Task DisposeAsync()
    {
        if (admin is null) return;
        await new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin).ExecuteNonQueryAsync();
        await admin.DisposeAsync();
    }
    private async Task<Guid> Ready()
    {
        await using var db = Context();
        await db.Database.MigrateAsync();
        var user = new User();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }
    private async Task<int?> Resolve(Guid owner, string url)
    {
        await using var db = Context();
        return await new TargetResolver(db).ResolveAsync(owner, url);
    }
    private static ScanExecution Scan(Guid? owner, string url, int? target = null) => new()
    {
        OwnerUserId = owner, Target = url, TargetId = target, StartedAt = DateTimeOffset.UtcNow,
        Status = ScanStatus.Succeeded, ExitCode = 0, CompletedAt = DateTimeOffset.UtcNow,
        Findings = [new() { TemplateId = "test", Name = "Test", Severity = "high", MatchedAt = "http://localhost:8085", RawJson = "{\"evidence\":42}" }]
    };

    [PostgresFact]
    public async Task ConcurrentResolutionIsExactAndOwnerScoped()
    {
        var owner = await Ready();
        var other = await Ready();
        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Resolve(owner, "http://localhost:8085/Path?q=A")));
        Assert.NotNull(ids[0]);
        Assert.Single(ids.Distinct());
        var variants = new[] { "http://localhost:8085/path?q=A", "http://localhost:8085/Path?q=a", "https://localhost:8085/Path?q=A", "http://localhost:8085/Path" };
        foreach (var url in variants) Assert.NotEqual(ids[0], await Resolve(owner, url));
        Assert.NotEqual(ids[0], await Resolve(other, "http://localhost:8085/Path?q=A"));
        await using var db = Context();
        Assert.Null(await new TargetResolver(db).ResolveLegacyAsync(owner, "not a URL"));
        Assert.Equal(6, await db.Targets.CountAsync());
    }

    [PostgresFact]
    public async Task DatabaseRejectsCrossOwnerLinksNullOwnersAndReferencedDeletion()
    {
        var owner = await Ready();
        var other = await Ready();
        var target = await Resolve(owner, "http://localhost:8085");
        foreach (var wrongOwner in new Guid?[] { other, null })
        {
            await using var db = Context();
            db.Add(Scan(wrongOwner, "http://localhost:8085", target));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = Context()) { db.Add(Scan(owner, "http://localhost:8085", target)); await db.SaveChangesAsync(); }
        await using (var db = Context())
            await Assert.ThrowsAsync<PostgresException>(() => db.Targets.Where(x => x.Id == target).ExecuteDeleteAsync());
        await using (var db = Context())
            await Assert.ThrowsAsync<PostgresException>(() => db.ScanExecutions.ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerUserId, other)));
    }

    [PostgresFact]
    public async Task AssignmentIsAtomicAndLinksOnlyUsableUrls()
    {
        var owner = await Ready();
        var other = await Ready();
        int a, b, c;
        await using (var db = Context())
        {
            var first = Scan(null, "http://localhost:8085");
            var invalid = Scan(null, "legacy-host");
            var owned = Scan(other, "http://localhost:8085");
            db.AddRange(first, invalid, owned); await db.SaveChangesAsync();
            (a, b, c) = (first.Id, invalid.Id, owned.Id);
        }
        await using (var db = Context())
            await Assert.ThrowsAsync<ArgumentException>(() => new UserService(db).AssignUnownedAsync(owner, [a, c]));
        await using (var db = Context())
        {
            Assert.Null((await db.ScanExecutions.SingleAsync(x => x.Id == a)).OwnerUserId);
            Assert.Empty(await db.Targets.ToListAsync());
            await new UserService(db).AssignUnownedAsync(owner, [a, b]);
        }
        await using (var db = Context())
        {
            var first = await db.ScanExecutions.SingleAsync(x => x.Id == a);
            Assert.Equal(owner, first.OwnerUserId);
            Assert.Equal(await Resolve(owner, first.Target), first.TargetId);
            Assert.Null((await db.ScanExecutions.SingleAsync(x => x.Id == b)).TargetId);
            Assert.Equal(3, await db.Findings.CountAsync());
            await Assert.ThrowsAsync<ArgumentException>(() => new UserService(db).AssignUnownedAsync(other, [a]));
        }
    }

    [PostgresFact]
    public async Task AssignmentRollsBackTargetsAndOwnershipWhenLinkWriteFails()
    {
        var owner = await Ready();
        await using var db = Context();
        var execution = Scan(null, "http://localhost:8085");
        db.Add(execution); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_target_link() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."TargetId" IS NOT NULL THEN RAISE EXCEPTION 'Simulated link failure'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER reject_target_link BEFORE UPDATE ON scan_executions
                FOR EACH ROW EXECUTE FUNCTION reject_target_link();
            """);
        await Assert.ThrowsAsync<PostgresException>(() => new UserService(db).AssignUnownedAsync(owner, [execution.Id]));
        var saved = await db.ScanExecutions.SingleAsync();
        Assert.Null(saved.OwnerUserId); Assert.Null(saved.TargetId);
        Assert.Empty(await db.Targets.ToListAsync());
        Assert.Single(await db.Findings.ToListAsync());
    }

    [LifecycleFact]
    public async Task OwnedAndUnownedCliPathsLinkBeforeScannerStartup()
    {
        var owner = await Ready();
        await using var db = Context();
        // Missing executable verifies persisted startup failure without scanning any host.
        var scans = new ScanService(new NucleiRunner(new(), "/tmp/isotopeprobe-missing-" + Guid.NewGuid()), db);
        var owned = await scans.RunAsync("http://localhost:8085", "/captured/templates", ownerUserId: owner);
        var unowned = await scans.RunAsync("http://localhost:8085");
        Assert.Equal(ScanStatus.Failed, owned.Status);
        Assert.NotNull(owned.TargetId);
        Assert.Equal(owner, (await db.Targets.SingleAsync()).OwnerUserId);
        Assert.Equal("/captured/templates", owned.TemplatePath);
        Assert.Null(unowned.TargetId);
        Assert.Null(unowned.OwnerUserId);
        var hostOnly = await scans.RunAsync("localhost:8085", ownerUserId: owner);
        Assert.NotNull(hostOnly.TargetId);
        Assert.Equal("localhost:8085", (await db.Targets.SingleAsync(x => x.Id == hostOnly.TargetId)).Url);
    }

    [LifecycleFact]
    public async Task WebAllowlistStillGatesStoredTargetsAndInputsAreSnapshots()
    {
        var owner = await Ready();
        var target = await Resolve(owner, "http://localhost:8085");
        await using var db = Context();
        var options = new WebScanOptions { Targets = [new() { Id = "allowed", Name = "Sanity", Url = "http://localhost:8085" }], TemplatePath = "/captured/templates", TimeoutSeconds = 35 };
        var service = new OwnedScanSubmissionService(db, new(owner), options);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitAsync(target.ToString()!, Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitAsync("http://localhost:8085", Guid.NewGuid()));
        var nonce = Guid.NewGuid();
        var id = await service.SubmitAsync("allowed", nonce);
        Assert.Equal(id, await service.SubmitAsync("allowed", nonce));
        options.Targets.Clear(); options.TemplatePath = "/changed"; options.TimeoutSeconds = 1;
        await new WebScanDispatcher(db, new(new(new(), "/tmp/isotopeprobe-missing-" + Guid.NewGuid()), db)).RunNextAsync(default);
        var saved = await db.ScanExecutions.SingleAsync(x => x.Id == id);
        Assert.Equal(target, saved.TargetId);
        Assert.Equal(owner, saved.OwnerUserId);
        Assert.Equal("http://localhost:8085", saved.Target);
        Assert.Equal("/captured/templates", saved.TemplatePath);
        Assert.Equal(35, saved.TimeoutSeconds);
        Assert.Equal(ScanStatus.Failed, saved.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SubmitAsync("allowed", Guid.NewGuid()));
        Assert.NotNull(await new OwnedTargetQueryService(db, new(owner)).GetAsync(target!.Value));
    }

    [PostgresFact]
    public async Task SummariesAndHistoryArePaginatedAndOwned()
    {
        var owner = await Ready();
        var other = await Ready();
        var target = (await Resolve(owner, "http://localhost:8085"))!.Value;
        var empty = (await Resolve(owner, "http://localhost:8085/empty"))!.Value;
        var hidden = (await Resolve(other, "http://localhost:8085"))!.Value;
        await using var db = Context();
        var first = Scan(owner, "http://localhost:8085", target);
        first.StartedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var latest = Scan(owner, "http://localhost:8085", target);
        latest.Findings.Clear(); latest.Status = ScanStatus.Queued; latest.StartedAt = null;
        latest.EnqueuedAt = DateTimeOffset.Parse("2026-02-01T00:00:00Z");
        db.AddRange(first, latest, Scan(other, "http://localhost:8085", hidden)); await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var queries = new OwnedTargetQueryService(db, new(owner));
        var page = await queries.ListAsync(0, 1);
        Assert.Equal(2, page.TotalCount); Assert.Single(page.Items);
        Assert.NotEqual(page.Items[0].Id, Assert.Single((await queries.ListAsync(1, 1)).Items).Id);
        Assert.Empty((await queries.ListAsync(2, 1)).Items);
        var summary = await queries.GetAsync(target);
        Assert.Equal(2, summary!.ExecutionCount);
        Assert.Equal(latest.Id, summary.LatestExecution!.Id);
        Assert.Equal(0, summary.LatestExecution.FindingCount);
        Assert.Equal(ScanStatus.Queued, summary.LatestExecution.Status);
        Assert.Null((await queries.GetAsync(empty))!.LatestExecution);
        Assert.Equal(latest.Id, Assert.Single((await queries.HistoryAsync(target, 0, 1))!.Items).Id);
        Assert.Equal(first.Id, Assert.Single((await queries.HistoryAsync(target, 1, 1))!.Items).Id);
        Assert.Null(await queries.GetAsync(hidden)); Assert.Null(await queries.HistoryAsync(hidden));
        Assert.Null(await queries.GetAsync(int.MaxValue));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [PostgresFact]
    public async Task UpgradeBackfillsAndPreservesPreviousMigrationData()
    {
        await using var db = Context();
        await db.GetService<IMigrator>().MigrateAsync("20260918205901_AddWebScanQueue");
        var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users ("Id", "CreatedAt") VALUES ({owner}, CURRENT_TIMESTAMP), ({other}, CURRENT_TIMESTAMP);
            """);
        // Use the previous schema directly; the current entity includes TargetId.
        foreach (var item in new[] { (1, (Guid?)owner, "http://localhost:8085/Path?q=A", "Succeeded"),
            (2, (Guid?)owner, "http://localhost:8085/Path?q=A", "Queued"),
            (3, (Guid?)other, "http://localhost:8085/Path?q=A", "Running"),
            (4, (Guid?)null, "http://localhost:8085/Path?q=A", "Failed"),
            (5, (Guid?)owner, "", "Cancelled"), (6, (Guid?)owner, "not a URL", "Failed"),
            (7, (Guid?)owner, "http://localhost:8085/path?q=A", "Succeeded") })
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO scan_executions ("Id", "OwnerUserId", "Target", "Status", "StandardError", "Source", "EnqueuedAt", "TemplatePath", "TemplateProfile", "TimeoutSeconds")
                VALUES ({item.Item1}, {item.Item2}, {item.Item3}, {item.Item4}, 'evidence', 'Web', '2026-01-01T00:00:00Z', '/snapshot', 'Sanity', 60);
                """);
        }
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE scan_executions SET "StartedAt" = '2026-01-01T00:00:01Z' WHERE "Status" <> 'Queued';
            UPDATE scan_executions SET "CompletedAt" = '2026-01-01T00:00:03Z', "ExitCode" = 0 WHERE "Status" = 'Succeeded';
            UPDATE scan_executions SET "FailureReason" = 'stored failure', "ExitCode" = 1 WHERE "Status" = 'Failed';
            UPDATE scan_executions SET "SubmissionId" = '11111111-1111-1111-1111-111111111111' WHERE "Id" = 2;
            INSERT INTO findings ("Id", "ScanExecutionId", "TemplateId", "Name", "Severity", "MatchedAt", "Authors", "Tags", "RawJson")
            VALUES (1, 1, 'test', 'Test', 'high', 'http://localhost:8085', ARRAY[]::text[], ARRAY[]::text[], jsonb_build_object('saved', true));
            """);
        async Task<string> Evidence() => await db.Database.SqlQueryRaw<string>("""
            SELECT jsonb_build_object('scans', (SELECT jsonb_agg(to_jsonb(e) - 'TargetId' ORDER BY "Id") FROM scan_executions e),
                'findings', (SELECT jsonb_agg(to_jsonb(f) - 'MatcherName' ORDER BY "Id") FROM findings f),
                'users', (SELECT jsonb_agg(to_jsonb(u) ORDER BY "Id") FROM users u))::text AS "Value"
            """).SingleAsync();
        var before = await Evidence();
        var started = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.Database.MigrateAsync();
        Assert.Equal(before, await Evidence());
        var targets = await db.Targets.ToListAsync();
        Assert.Equal(3, targets.Count);
        Assert.All(targets, t => { Assert.Equal(t.Url, t.Name); Assert.InRange(t.CreatedAtUtc, started, DateTimeOffset.UtcNow); });
        var scans = await db.ScanExecutions.OrderBy(x => x.Id).ToListAsync();
        Assert.NotNull(scans[0].TargetId);
        Assert.Equal(scans[0].TargetId, scans[1].TargetId);
        Assert.NotEqual(scans[0].TargetId, scans[2].TargetId);
        Assert.NotEqual(scans[0].TargetId, scans[6].TargetId);
        Assert.All(scans.Skip(3).Take(3), e => Assert.Null(e.TargetId));
        Assert.Equal(ScanStatus.Queued, scans[1].Status);
        Assert.Equal(ScanStatus.Running, scans[2].Status);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
