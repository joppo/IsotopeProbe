using IsotopeProbe.Domain;
using IsotopeProbe.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IsotopeProbe.Queue;

// Trusted local CLI only; never registered with the Web host.
public sealed class WebScanRecovery(IsotopeProbeDbContext db)
{
    public async Task MarkInterruptedFailedAsync(int id, CancellationToken token = default)
    {
        if (id <= 0) throw new ArgumentException("Execution ID must be positive.");
        var updated = await db.ScanExecutions.Where(x => x.Id == id && x.Source == ScanSource.Web && x.Status == ScanStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ScanStatus.Failed)
                .SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow)
                .SetProperty(x => x.FailureReason, "Interrupted Web execution recovered by local operator after confirming the process stopped."), token);
        if (updated != 1) throw new ArgumentException("Selected execution is not a Running Web execution; nothing changed.");
    }
}
