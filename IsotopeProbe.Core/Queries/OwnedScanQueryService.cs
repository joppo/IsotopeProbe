using IsotopeProbe.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IsotopeProbe.Queries;

// Web supplies this context from its validated application session, never request parameters.
public sealed record ScanUser
{
    public Guid Id { get; }
    public ScanUser(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A local user ID is required.");
        Id = id;
    }
}

public sealed class OwnedScanQueryService(IsotopeProbeDbContext db, ScanUser user)
{
    private readonly ScanQueryService queries = new(
        db.ScanExecutions.AsNoTracking().Where(x => x.OwnerUserId == user.Id),
        db.Findings.AsNoTracking().Where(x => x.ScanExecution.OwnerUserId == user.Id));
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
