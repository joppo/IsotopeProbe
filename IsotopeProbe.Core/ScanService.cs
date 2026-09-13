using IsotopeProbe.Domain;
using IsotopeProbe.Nuclei;
using IsotopeProbe.Persistence;

namespace IsotopeProbe;

public sealed class ScanService(NucleiRunner runner, IsotopeProbeDbContext db)
{
    public async Task<ScanExecution> RunAsync(
        string target, string? templatePath = null,
        CancellationToken cancellationToken = default)
    {
        var execution = await runner.RunAsync(target, templatePath, cancellationToken);
        db.ScanExecutions.Add(execution);
        await db.SaveChangesAsync(cancellationToken);
        return execution;
    }
}
