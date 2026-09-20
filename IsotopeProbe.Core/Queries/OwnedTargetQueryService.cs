using IsotopeProbe.Persistence;
using IsotopeProbe.Domain;
using Microsoft.EntityFrameworkCore;

namespace IsotopeProbe.Queries;

public sealed record TargetSummary(int Id, string Name, string Url, DateTimeOffset CreatedAtUtc,
    int ExecutionCount, ScanSummary? LatestExecution);

public sealed class OwnedTargetQueryService(IsotopeProbeDbContext db, ScanUser user)
{
    private IQueryable<Target> OwnedTargets => db.Targets.AsNoTracking().Where(t => t.OwnerUserId == user.Id);

    private IQueryable<TargetSummary> Summaries(IQueryable<Target> targets) => targets.Select(t => new TargetSummary(t.Id, t.Name, t.Url, t.CreatedAtUtc,
            db.ScanExecutions.Count(e => e.OwnerUserId == user.Id && e.TargetId == t.Id),
            db.ScanExecutions.Where(e => e.OwnerUserId == user.Id && e.TargetId == t.Id)
                .OrderByDescending(e => e.EnqueuedAt ?? e.StartedAt ?? DateTimeOffset.MinValue).ThenByDescending(e => e.Id)
                .Select(e => new ScanSummary(e.Id, e.Target, e.StartedAt, e.CompletedAt, e.ExitCode,
                    e.Findings.Count, e.Status, e.EnqueuedAt, e.TemplateProfile, e.TargetId)).FirstOrDefault()));

    public async Task<Page<TargetSummary>> ListAsync(int skip = 0, int take = 20, CancellationToken token = default)
    {
        ScanQueryService.ValidatePagination(skip, take);
        var targets = OwnedTargets;
        var count = await targets.CountAsync(token);
        var items = await Summaries(targets.OrderByDescending(t => t.CreatedAtUtc).ThenByDescending(t => t.Id)
            .Skip(skip).Take(take)).ToListAsync(token);
        return new(items, count, skip, take);
    }

    public Task<TargetSummary?> GetAsync(int id, CancellationToken token = default)
    {
        ScanQueryService.ValidateScanId(id);
        return Summaries(OwnedTargets.Where(t => t.Id == id)).SingleOrDefaultAsync(token);
    }

    public async Task<Page<ScanSummary>?> HistoryAsync(int id, int skip = 0, int take = 20, CancellationToken token = default)
    {
        ScanQueryService.ValidateScanId(id);
        ScanQueryService.ValidatePagination(skip, take);
        if (!await db.Targets.AnyAsync(t => t.OwnerUserId == user.Id && t.Id == id, token)) return null;
        var executions = db.ScanExecutions.AsNoTracking().Where(e => e.OwnerUserId == user.Id && e.TargetId == id);
        return await new ScanQueryService(executions,
            db.Findings.AsNoTracking().Where(f => f.ScanExecution.OwnerUserId == user.Id && f.ScanExecution.TargetId == id))
            .ListScansAsync(skip, take, token);
    }
}
