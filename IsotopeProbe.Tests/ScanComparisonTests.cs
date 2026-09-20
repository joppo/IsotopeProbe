using IsotopeProbe.Comparisons;
using IsotopeProbe.Domain;
using IsotopeProbe.Persistence;
using IsotopeProbe.Queries;
using IsotopeProbe.Targets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace IsotopeProbe.Tests;

public sealed class ScanComparisonTests : IAsyncLifetime
{
    private readonly string schema = "comparison_test_" + Guid.NewGuid().ToString("N");
    private NpgsqlConnection? admin;
    private string connection = "";
    private IsotopeProbeDbContext Context() => new(new DbContextOptionsBuilder<IsotopeProbeDbContext>().UseNpgsql(connection).Options);
    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ISOTOPEPROBE_TEST_CONNECTION_STRING");
        if (string.IsNullOrEmpty(configured)) return;
        admin = new(configured); await admin.OpenAsync();
        await new NpgsqlCommand($"CREATE SCHEMA {schema}", admin).ExecuteNonQueryAsync();
        connection = new NpgsqlConnectionStringBuilder(configured) { SearchPath = schema, Pooling = false }.ConnectionString;
    }
    public async Task DisposeAsync()
    {
        if (admin is null) return;
        await new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin).ExecuteNonQueryAsync();
        await admin.DisposeAsync();
    }
    private async Task<(Guid Owner, int Target)> Ready(IsotopeProbeDbContext db)
    {
        await db.Database.MigrateAsync();
        var user = new User(); db.Add(user); await db.SaveChangesAsync();
        return (user.Id, await new TargetResolver(db).ResolveAsync(user.Id, "http://localhost:8085"));
    }
    private static ScanExecution Scan(Guid owner, int? target, int hour = 0, ScanStatus status = ScanStatus.Succeeded) => new()
    {
        OwnerUserId = owner, TargetId = target, Target = "http://localhost:8085", Status = status,
        StartedAt = DateTimeOffset.Parse("2026-09-01T00:00:00Z").AddHours(hour),
        CompletedAt = DateTimeOffset.Parse("2026-09-01T00:00:01Z").AddHours(hour),
        TemplatePath = "/snapshot/templates", TemplateProfile = "Sanity"
    };
    private static Finding F(string template, string? matcher = null, string severity = "high") => new()
    {
        TemplateId = template, MatchedAt = "http://localhost:8085/Path?q=A", MatcherName = matcher,
        Severity = severity, Name = "Test", RawJson = "{\"evidence\":\"unchanged\"}"
    };

    [PostgresFact]
    public async Task LocalFixturesDemonstrateNewBothAndNoLongerDetected()
    {
        await using var db = Context(); var (owner, target) = await Ready(db);
        var older = Scan(owner, target); var newer = Scan(owner, target, 1);
        var parser = new IsotopeProbe.Nuclei.NucleiFindingParser();
        older.Findings = (await File.ReadAllLinesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures/Comparison/baseline.jsonl"))).Select(parser.Parse).ToList();
        newer.Findings = (await File.ReadAllLinesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures/Comparison/newer.jsonl"))).Select(parser.Parse).ToList();
        db.AddRange(older, newer); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var result = await new OwnedScanComparisonService(db, new(owner)).CompareAsync(older.Id, newer.Id, ComparisonCategory.Both);
        Assert.Equal(new ComparisonCounts(1, 1, 1, 0, 0, 3, 2, 0), result!.Counts);
        Assert.Equal(2, Assert.Single(result.Items.Items).Older.Count);
        Assert.Equal("body", result.Items.Items[0].Key!.MatcherName);
        Assert.Equal("low", Assert.Single(result.Items.Items[0].Newer).Severity);
        Assert.Equal(5, await db.Findings.CountAsync());
    }

    [PostgresFact]
    public async Task CountsPaginationEvidenceAndWarningsAreConsistent()
    {
        await using var db = Context(); var (owner, target) = await Ready(db);
        var older = Scan(owner, target); var newer = Scan(owner, target, 1);
        older.Findings = [F("both"), F("both"), F("removed"), F("metadata"), F("")];
        newer.Findings = [F("both", severity: "low"), F("new-a"), F("new-b"), F("metadata", "named"), F("bad")];
        newer.Findings[^1].MatchedAt = " "; newer.TemplateProfile = "Changed";
        db.AddRange(older, newer); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var queries = new OwnedScanComparisonService(db, new(owner));
        var page = await queries.CompareAsync(older.Id, newer.Id, take: 1);
        Assert.Equal(new ComparisonCounts(3, 1, 2, 1, 1, 5, 5, 1), page!.Counts);
        Assert.Equal(3, page.Items.TotalCount); Assert.Single(page.Items.Items);
        var next = await queries.CompareAsync(older.Id, newer.Id, skip: 1, take: 1);
        Assert.Equal(page.Counts, next!.Counts); Assert.NotEqual(page.Items.Items[0].Key, next.Items.Items[0].Key);
        Assert.Empty((await queries.CompareAsync(older.Id, newer.Id, skip: 3))!.Items.Items);
        Assert.Contains(page.Warnings, x => x.Contains("configuration differs"));
        Assert.Contains(page.Warnings, x => x.Contains("Coverage cannot be established"));
        Assert.Contains(page.Warnings, x => x.Contains("Matcher-name availability differs"));
        var both = Assert.Single((await queries.CompareAsync(older.Id, newer.Id, ComparisonCategory.Both))!.Items.Items);
        var evidence = await queries.EvidenceAsync(older.Id, newer.Id, true, both.Older[0].Id, take: 1);
        Assert.Equal(2, evidence!.Records.TotalCount); Assert.Single(evidence.Records.Items);
        Assert.NotEqual(evidence.Records.Items[0].Id, Assert.Single((await queries.EvidenceAsync(older.Id, newer.Id, true, both.Older[0].Id, 1, 1))!.Records.Items).Id);
        Assert.Null(await queries.EvidenceAsync(older.Id, newer.Id, false, both.Older[0].Id));
        Assert.Equal("low", Assert.Single(both.Newer).Severity);
        Assert.Single((await queries.CompareAsync(older.Id, newer.Id, ComparisonCategory.IncompleteOlder))!.Items.Items);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [PostgresFact]
    public async Task PairChecksBothOwnersAndEligibilityBeforeReadingFindings()
    {
        await using var db = Context(); var (owner, target) = await Ready(db); var (other, otherTarget) = await Ready(db);
        var anotherTarget = await new TargetResolver(db).ResolveAsync(owner, "http://localhost:8085/other");
        var baseline = Scan(owner, target); var current = Scan(owner, target, 1);
        var privateScan = Scan(other, otherTarget); var wrongTarget = Scan(owner, anotherTarget);
        var unlinked = Scan(owner, null); var failed = Scan(owner, target, status: ScanStatus.Failed);
        var unfinished = Scan(owner, target); unfinished.CompletedAt = null;
        db.AddRange(baseline, current, privateScan, wrongTarget, unlinked, failed, unfinished); await db.SaveChangesAsync();
        var queries = new OwnedScanComparisonService(db, new(owner));
        Assert.Null(await queries.CompareAsync(privateScan.Id, current.Id));
        Assert.Null(await queries.CompareAsync(baseline.Id, privateScan.Id));
        Assert.Null(await queries.CompareAsync(int.MaxValue, current.Id));
        Assert.Null(await queries.CompareAsync(failed.Id, privateScan.Id));
        Assert.Null(await queries.BaselinesAsync(privateScan.Id));
        Assert.Null(await queries.PreviousSuccessfulAsync(privateScan.Id));
        Assert.Null(await queries.EvidenceAsync(privateScan.Id, current.Id, true, 1));
        foreach (var invalid in new[] { current, wrongTarget, unlinked, failed, unfinished })
            await Assert.ThrowsAsync<ArgumentException>(() => queries.CompareAsync(invalid.Id, current.Id));
    }

    [PostgresFact]
    public async Task PreviousSuccessfulUsesTimeThenIdAndBaselineSelectionIsBounded()
    {
        await using var db = Context(); var (owner, target) = await Ready(db);
        var first = Scan(owner, target); var tied = Scan(owner, target); var current = Scan(owner, target, 2);
        db.AddRange(first, tied, current, Scan(owner, target, 1, ScanStatus.Failed), Scan(owner, target, 1, ScanStatus.Cancelled),
            Scan(owner, target, 1, ScanStatus.Running), Scan(owner, target, 1, ScanStatus.Queued));
        await db.SaveChangesAsync();
        var queries = new OwnedScanComparisonService(db, new(owner));
        Assert.Equal(tied.Id, await queries.PreviousSuccessfulAsync(current.Id));
        Assert.Equal(first.Id, await queries.PreviousSuccessfulAsync(tied.Id));
        Assert.Null(await queries.PreviousSuccessfulAsync(first.Id));
        var page = await queries.BaselinesAsync(current.Id, take: 1);
        Assert.Equal(2, page!.TotalCount); Assert.Equal(tied.Id, Assert.Single(page.Items).Id);
        Assert.Equal(first.Id, Assert.Single((await queries.BaselinesAsync(current.Id, 1, 1))!.Items).Id);
        Assert.Empty((await queries.BaselinesAsync(current.Id, 2, 1))!.Items);
    }

    [PostgresFact]
    public async Task OversizedComparisonsAreRejectedRatherThanTruncated()
    {
        await using var db = Context(); var (owner, target) = await Ready(db);
        var older = Scan(owner, target); var newer = Scan(owner, target, 1); db.AddRange(older, newer); await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO findings ("ScanExecutionId", "TemplateId", "Name", "Severity", "MatchedAt", "Authors", "Tags", "RawJson")
            SELECT {newer.Id}, 'test', 'Test', 'info', 'http://localhost:8085', ARRAY[]::text[], ARRAY[]::text[], jsonb_build_object()
            FROM generate_series(1, {OwnedScanComparisonService.MaximumRecordsPerExecution + 1});
            """);
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => new OwnedScanComparisonService(db, new(owner)).CompareAsync(older.Id, newer.Id));
        Assert.Contains("not compared", exception.Message);
    }

    [PostgresFact]
    public async Task MatcherBackfillPreservesRawJsonAndHandlesUnsuitableShapes()
    {
        await using var db = Context();
        await db.GetService<IMigrator>().MigrateAsync("20260919133329_AddPersistentTargets");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO scan_executions ("Id", "Target", "Status", "StandardError", "Source") VALUES (1, 'http://localhost:8085', 'Succeeded', '', 'Cli');
            """);
        var raw = new[] { "{\"matcher-name\":\"Case Sensitive \"}", "{}", "{\"matcher-name\":null}",
            "{\"matcher-name\":123}", "{\"matcher-name\":{\"wrong\":true}}", "{\"matcher-name\":[]}",
            "{\"matcher-name\":\" \\t\\n\"}", "null", "[]", "\"malformed-looking: {bad}\"",
            "{\"nested\":{\"matcher-name\":\"ignored\"}}", "{\"matcher-name\":\"\\u00a0\\u2003\"}" };
        for (var i = 0; i < raw.Length; i++)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO findings ("Id", "ScanExecutionId", "TemplateId", "Name", "Severity", "MatchedAt", "Authors", "Tags", "RawJson", "MatcherStatus")
                VALUES ({i + 1}, 1, 'test', 'Test', 'info', 'http://localhost:8085', ARRAY[]::text[], ARRAY[]::text[], CAST({raw[i]} AS jsonb), true);
                """);
        var before = await db.Database.SqlQueryRaw<string>("""SELECT jsonb_agg(to_jsonb(f) ORDER BY "Id")::text AS "Value" FROM findings f""").SingleAsync();
        await db.Database.MigrateAsync();
        var after = await db.Database.SqlQueryRaw<string>("""SELECT jsonb_agg(to_jsonb(f) - 'MatcherName' ORDER BY "Id")::text AS "Value" FROM findings f""").SingleAsync();
        Assert.Equal(before, after);
        var findings = await db.Findings.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal("Case Sensitive ", findings[0].MatcherName);
        Assert.All(findings.Skip(1), x => Assert.Null(x.MatcherName));
        Assert.All(findings, x => Assert.True(x.MatcherStatus));
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
