using IsotopeProbe.Domain;
using IsotopeProbe.Persistence;
using IsotopeProbe.Queries;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IsotopeProbe.Tests;

// Real PostgreSQL verifies translation of projections, counts and pagination.
// Each test migrates a separate schema and removes only that schema afterwards.
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ISOTOPEPROBE_TEST_CONNECTION_STRING")))
            Skip = "Set ISOTOPEPROBE_TEST_CONNECTION_STRING to run isolated PostgreSQL query tests.";
    }
}

public sealed class ScanQueryServiceTests : IAsyncLifetime
{
    private readonly string _schema = "query_test_" + Guid.NewGuid().ToString("N");
    private NpgsqlConnection? _connection;
    private IsotopeProbeDbContext _db = null!;
    private ScanQueryService _queries = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ISOTOPEPROBE_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
            return;
        _connection = new NpgsqlConnection(connectionString);
        await _connection.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE SCHEMA {_schema}", _connection);
        await create.ExecuteNonQueryAsync();
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = _schema, Pooling = false };
        _db = new IsotopeProbeDbContext(new DbContextOptionsBuilder<IsotopeProbeDbContext>()
            .UseNpgsql(builder.ConnectionString).Options);
        _queries = new ScanQueryService(_db);
        try
        {
            await _db.Database.MigrateAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
            await _db.DisposeAsync();
        if (_connection is not null)
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {_schema} CASCADE", _connection);
            await drop.ExecuteNonQueryAsync();
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    [PostgresFact]
    public async Task ListScans_OrdersByTimeThenIdAndPaginatesWithCounts()
    {
        var older = Scan(10, 0);
        var newer = Scan(20, 1, "high", "low");
        var tied = Scan(30, 1);
        tied.ExitCode = 2;
        tied.Status = ScanStatus.Failed;
        await Seed(older, newer, tied);

        var page = await _queries.ListScansAsync(0, 2);
        Assert.Equal(new[] { 30, 20 }, page.Items.Select(x => x.Id));
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(0, page.Items[0].FindingCount);
        Assert.False(page.Items[0].Succeeded);
        Assert.Equal(2, page.Items[1].FindingCount);
        Assert.True(page.Items[1].Succeeded);
        var next = await _queries.ListScansAsync(2, 2);
        Assert.Equal(10, Assert.Single(next.Items).Id);
        var beyond = await _queries.ListScansAsync(3, 2);
        Assert.Empty(beyond.Items);
        Assert.Equal(3, beyond.TotalCount);
        Assert.Empty(_db.ChangeTracker.Entries());
    }

    [PostgresFact]
    public async Task ListFindings_FiltersBeforeCountingAndPagingInIdOrder()
    {
        var first = Scan(10, 0, "high", "low", "high");
        first.Findings[0].Id = 103;
        first.Findings[1].Id = 101;
        first.Findings[2].Id = 102;
        var other = Scan(20, 1, "critical");
        other.Findings[0].Id = 100;
        await Seed(first, other);

        var page = await _queries.ListFindingsAsync(10, 1, 1);
        Assert.NotNull(page);
        Assert.Equal(3, page.TotalCount);
        var finding = Assert.Single(page.Items);
        Assert.Equal(102, finding.Id);
        Assert.Equal("high", finding.Severity);
        Assert.Equal("template", finding.TemplateId);
        Assert.Equal("http://localhost/test", finding.MatchedAt);
        var all = await _queries.ListFindingsAsync(10);
        Assert.Equal(new[] { 101, 102, 103 }, all!.Items.Select(x => x.Id));
        var beyond = await _queries.ListFindingsAsync(10, 3);
        Assert.Empty(beyond!.Items);
        Assert.Equal(3, beyond.TotalCount);
        Assert.Empty(_db.ChangeTracker.Entries());
    }

    [PostgresFact]
    public async Task GetScan_FiltersMetadataAndAggregatesSeverityCounts()
    {
        var scan = Scan(10, 0, "high", "high", "low");
        scan.ExitCode = 2;
        scan.Status = ScanStatus.Failed;
        scan.StandardError = "scanner diagnostic";
        await Seed(scan, Scan(20, 1, "critical"));

        var details = await _queries.GetScanAsync(10);
        Assert.NotNull(details);
        Assert.Equal(10, details.Execution.Id);
        Assert.Equal(scan.Target, details.Execution.Target);
        Assert.Equal(scan.StartedAt, details.Execution.StartedAt);
        Assert.Equal(scan.CompletedAt, details.Execution.CompletedAt);
        Assert.Equal(2, details.Execution.ExitCode);
        Assert.Equal("scanner diagnostic", details.StandardError);
        Assert.Equal(3, details.Execution.FindingCount);
        Assert.Equal(new[] { new SeverityCount("high", 2), new SeverityCount("low", 1) }, details.Severities);
        Assert.Empty(_db.ChangeTracker.Entries());
    }

    [PostgresFact]
    public async Task Queries_DistinguishMissingScanFromEmptyScanAndEmptyDatabase()
    {
        var scans = await _queries.ListScansAsync();
        Assert.Empty(scans.Items);
        Assert.Equal(0, scans.TotalCount);
        Assert.Null(await _queries.GetScanAsync(99));
        Assert.Null(await _queries.ListFindingsAsync(99));
        await Seed(Scan(10, 0));
        var details = await _queries.GetScanAsync(10);
        Assert.NotNull(details);
        Assert.Equal(0, details.Execution.FindingCount);
        Assert.Empty(details.Severities);
        var findings = await _queries.ListFindingsAsync(10);
        Assert.NotNull(findings);
        Assert.Empty(findings.Items);
        Assert.Equal(0, findings.TotalCount);
    }

    [PostgresFact]
    public async Task Queries_HonorCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _queries.ListScansAsync(cancellationToken: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _queries.GetScanAsync(1, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _queries.ListFindingsAsync(1, cancellationToken: cancellation.Token));
    }

    [PostgresFact]
    public async Task SeverityFilter_IsAppliedWithinExecutionBeforeCountingAndPagination()
    {
        var scan = Scan(10, 0, "high", "low", "high", "high");
        for (var i = 0; i < scan.Findings.Count; i++) scan.Findings[i].Id = 101 + i;
        await Seed(scan, Scan(20, 1, "high"));
        var page = await _queries.ListFindingsAsync(10, 1, 1, severity: "high");
        Assert.Equal(3, page!.TotalCount);
        Assert.Equal(103, Assert.Single(page.Items).Id);
        Assert.Empty((await _queries.ListFindingsAsync(10, 3, severity: "high"))!.Items);
        Assert.Equal(0, (await _queries.ListFindingsAsync(10, severity: "missing"))!.TotalCount);
        Assert.Null(await _queries.ListFindingsAsync(99, severity: "high"));
        Assert.Equal(4, (await _queries.GetScanAsync(10))!.Execution.FindingCount);
        Assert.Equal(2, (await _queries.GetScanAsync(10))!.Severities.Count);
        Assert.Empty(_db.ChangeTracker.Entries());
    }

    [PostgresFact]
    public async Task GetFinding_ReturnsStoredDetailsWithoutTrackingAndHandlesMissingId()
    {
        var scan = Scan(10, 0, "high");
        var finding = scan.Findings[0];
        finding.Id = 101;
        finding.Request = "<script>alert('request')</script>";
        finding.Response = "<html>untrusted</html>";
        finding.Authors = ["author"];
        finding.Tags = ["tag"];
        finding.RawJson = "{\"info\":{\"description\":\"<b>description</b>\"},\"extracted-results\":[\"evidence\"]}";
        await Seed(scan, Scan(20, 1, "low"));
        var result = await _queries.GetFindingAsync(101);
        Assert.NotNull(result);
        Assert.Equal(10, result.ScanExecutionId);
        Assert.Equal(finding.TemplateId, result.TemplateId);
        Assert.Equal(finding.Request, result.Request);
        Assert.Equal(finding.Response, result.Response);
        Assert.Equal(finding.Authors, result.Authors);
        Assert.Equal(finding.Tags, result.Tags);
        using var json = System.Text.Json.JsonDocument.Parse(result.RawJson);
        Assert.Equal("<b>description</b>", json.RootElement.GetProperty("info").GetProperty("description").GetString());
        Assert.Null(await _queries.GetFindingAsync(999));
        Assert.Empty(_db.ChangeTracker.Entries());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _queries.GetFindingAsync(101, cancellation.Token));
    }

    private async Task Seed(params ScanExecution[] scans)
    {
        _db.ScanExecutions.AddRange(scans);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private static ScanExecution Scan(int id, int day, params string[] severities) => new()
    {
        Status = ScanStatus.Succeeded, ExitCode = 0,
        Id = id, Target = $"http://localhost/{id}",
        StartedAt = DateTimeOffset.Parse("2026-09-01T00:00:00Z").AddDays(day),
        CompletedAt = DateTimeOffset.Parse("2026-09-01T00:01:00Z").AddDays(day),
        Findings = severities.Select(severity => new Finding
        {
            RawJson = "{}", TemplateId = "template", Name = "Test", Severity = severity,
            MatchedAt = "http://localhost/test"
        }).ToList()
    };
}
