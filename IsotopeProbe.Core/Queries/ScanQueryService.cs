using IsotopeProbe.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IsotopeProbe.Queries;

public sealed class ScanQueryService(IsotopeProbeDbContext db)
{
    public const int DefaultTake = 20;
    public const int MaximumTake = 100;

    public async Task<Page<ScanSummary>> ListScansAsync(
        int skip = 0, int take = DefaultTake, CancellationToken cancellationToken = default)
    {
        ValidatePagination(skip, take);
        var scans = db.ScanExecutions.AsNoTracking();
        var total = await scans.CountAsync(cancellationToken);
        var items = await scans.OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id)
            .Skip(skip).Take(take)
            .Select(x => new ScanSummary(x.Id, x.Target, x.StartedAt, x.CompletedAt,
                x.ExitCode, x.Findings.Count, x.Status))
            .ToListAsync(cancellationToken);
        return new Page<ScanSummary>(items, total, skip, take);
    }

    public async Task<ScanDetails?> GetScanAsync(int id, CancellationToken cancellationToken = default)
    {
        ValidateScanId(id);
        var scan = await db.ScanExecutions.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new
            {
                Execution = new ScanSummary(x.Id, x.Target, x.StartedAt, x.CompletedAt,
                    x.ExitCode, x.Findings.Count, x.Status),
                x.StandardError,
                x.FailureReason
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (scan is null)
            return null;

        var severities = await db.Findings.AsNoTracking().Where(x => x.ScanExecutionId == id)
            .GroupBy(x => x.Severity).OrderBy(x => x.Key)
            .Select(x => new SeverityCount(x.Key, x.Count()))
            .ToListAsync(cancellationToken);
        return new ScanDetails(scan.Execution, scan.StandardError, scan.FailureReason, severities);
    }

    public async Task<Page<FindingSummary>?> ListFindingsAsync(
        int scanId, int skip = 0, int take = DefaultTake, CancellationToken cancellationToken = default)
    {
        ValidateScanId(scanId);
        ValidatePagination(skip, take);
        if (!await db.ScanExecutions.AsNoTracking().AnyAsync(x => x.Id == scanId, cancellationToken))
            return null;

        var findings = db.Findings.AsNoTracking().Where(x => x.ScanExecutionId == scanId);
        var total = await findings.CountAsync(cancellationToken);
        // Findings have no consistently populated scan-time timestamp. Their unique ID
        // provides a stable order within an execution.
        var items = await findings.OrderBy(x => x.Id).Skip(skip).Take(take)
            .Select(x => new FindingSummary(x.Id, x.TemplateId, x.Name, x.Severity, x.MatchedAt))
            .ToListAsync(cancellationToken);
        return new Page<FindingSummary>(items, total, skip, take);
    }

    public static void ValidatePagination(int skip, int take)
    {
        if (skip < 0)
            throw new ArgumentOutOfRangeException(nameof(skip), "Skip must be zero or greater.");
        if (take is < 1 or > MaximumTake)
            throw new ArgumentOutOfRangeException(nameof(take), $"Take must be between 1 and {MaximumTake}.");
    }

    public static void ValidateScanId(int id)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id), "Scan ID must be a positive integer.");
    }
}
