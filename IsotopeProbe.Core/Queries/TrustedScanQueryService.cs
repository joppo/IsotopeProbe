using IsotopeProbe.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IsotopeProbe.Queries;

// Trusted operator interface: never register this in Web.
public sealed class TrustedScanQueryService(IsotopeProbeDbContext db)
{
    private readonly ScanQueryService queries = new(db.ScanExecutions.AsNoTracking(), db.Findings.AsNoTracking());
    public const int DefaultTake = 20;
    public const int MaximumTake = 100;
    public Task<Page<ScanSummary>> ListScansAsync(int skip = 0, int take = DefaultTake, CancellationToken cancellationToken = default) =>
        queries.ListScansAsync(skip, take, cancellationToken);
    public Task<ScanDetails?> GetScanAsync(int id, CancellationToken cancellationToken = default) => queries.GetScanAsync(id, cancellationToken);
    public Task<Page<FindingSummary>?> ListFindingsAsync(int scanId, int skip = 0, int take = DefaultTake, CancellationToken cancellationToken = default, string? severity = null) =>
        queries.ListFindingsAsync(scanId, skip, take, cancellationToken, severity);
    public Task<FindingDetails?> GetFindingAsync(int id, CancellationToken cancellationToken = default) => queries.GetFindingAsync(id, cancellationToken);
    public static void ValidatePagination(int skip, int take) => ScanQueryService.ValidatePagination(skip, take);
    public static void ValidateScanId(int id) => ScanQueryService.ValidateScanId(id);
}
