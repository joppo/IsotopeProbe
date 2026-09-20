using IsotopeProbe.Domain;
using IsotopeProbe.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IsotopeProbe.Queue;

// Worker-only service. Each call uses a fresh scope/DbContext and runs at most one job.
public sealed class WebScanDispatcher(IsotopeProbeDbContext db, ScanService scans)
{
    public async Task<bool> RunNextAsync(CancellationToken stoppingToken)
    {
        stoppingToken.ThrowIfCancellationRequested();
        ScanExecution? execution;
        await using (var transaction = await db.Database.BeginTransactionAsync(stoppingToken))
        {
            execution = (await db.ScanExecutions.FromSqlRaw("""
                SELECT * FROM scan_executions
                WHERE "Source" = 'Web' AND "Status" = 'Queued'
                ORDER BY "EnqueuedAt", "Id" LIMIT 1 FOR UPDATE SKIP LOCKED
                """).ToListAsync(stoppingToken)).SingleOrDefault();
            if (execution is null) return false;
            execution.Status = ScanStatus.Running;
            execution.StartedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(stoppingToken);
            await transaction.CommitAsync(stoppingToken);
        }
        // No HTTP state survives admission. Snapshot timeout/profile/target are on the row.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(execution.TimeoutSeconds!.Value));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);
        await scans.ExecuteAsync(execution, linked.Token, () => stoppingToken.IsCancellationRequested
            ? (ScanStatus.Cancelled, "Application shutdown requested.")
            : (ScanStatus.Failed, $"Execution timed out after {execution.TimeoutSeconds} seconds."));
        return true;
    }
}
