using IsotopeProbe.Domain;
using IsotopeProbe.Nuclei;
using IsotopeProbe.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IsotopeProbe;

public sealed class ScanService(NucleiRunner runner, IsotopeProbeDbContext db, Profiles.ProfileCatalog? profiles = null)
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    public async Task<ScanExecution> RunAsync(
        string target, string? templatePath = null,
        CancellationToken cancellationToken = default, Guid? ownerUserId = null, string? profileId = null)
    {
        if (ownerUserId is Guid owner)
            await new Identity.UserService(db).RequireUserAsync(owner, cancellationToken);
        int? targetId = ownerUserId is Guid targetOwner
            ? await new Targets.TargetResolver(db).ResolveAsync(targetOwner, target, token: cancellationToken)
            : null;
        var execution = new ScanExecution { TargetId = targetId, Target = target, StartedAt = DateTimeOffset.UtcNow, OwnerUserId = ownerUserId, TemplatePath = templatePath };
        if (profileId is not null)
        {
            if (templatePath is not null) throw new ArgumentException("Profile and template path cannot be combined.");
            (profiles ?? throw new ArgumentException("Profile catalog is required.")).GetPrepared(profileId).Capture(execution);
        }
        db.ScanExecutions.Add(execution);
        try
        {
            using var startWrite = new CancellationTokenSource(WriteTimeout);
            await db.SaveChangesAsync(startWrite.Token);
        }
        catch
        {
            throw new ScanPersistenceException("Could not confirm the Running execution was saved. Nuclei was not launched.");
        }

        return await ExecuteAsync(execution, cancellationToken);
    }

    // Only queue orchestration may execute a claimed record. CLI still creates its own.
    internal async Task<ScanExecution> ExecuteAsync(ScanExecution execution,
        CancellationToken cancellationToken, Func<(ScanStatus Status, string Reason)?>? cancellationOutcome = null)
    {
        NucleiRunResult result;
        var verifyingSnapshot = false;
        try
        {
            string[]? templateFiles = null;
            if (execution.ProfileId is not null || execution.SnapshotHash is not null)
            {
                verifyingSnapshot = true;
                if (execution.SnapshotHash is null) throw new ArgumentException("Recorded profile snapshot reference is missing.");
                templateFiles = (profiles ?? throw new ArgumentException("Snapshot storage is not configured.")).Verify(
                    execution.SnapshotHash, execution.ProfileId, execution.ProfileVersion);
                verifyingSnapshot = false;
                execution.NucleiVersion = await runner.GetVersionAsync(cancellationToken);
            }
            result = await runner.RunAsync(execution.Target, execution.TemplatePath, async finding =>
            {
                finding.ScanExecutionId = execution.Id;
                db.Findings.Add(finding);
                using var findingWrite = new CancellationTokenSource(WriteTimeout);
                await db.SaveChangesAsync(findingWrite.Token);
            }, cancellationToken, templateFiles);
        }
        catch (Exception exception)
        {
            // Also handles setup errors before the runner enters its process loop.
            result = new NucleiRunResult(null, "",
                verifyingSnapshot ? "Template snapshot verification failed: content or reference is missing, altered or unavailable; no fallback was used."
                    : $"Scanner setup failed ({exception.GetType().Name}).", cancellationToken.IsCancellationRequested);
        }

        // This is the terminal decision point. Cancellation during the final write
        // does not reverse an already chosen outcome or cancel its persistence.
        var status = result.Cancelled ? ScanStatus.Cancelled
            : result.FailureReason is not null || result.ExitCode != 0 ? ScanStatus.Failed
            : cancellationToken.IsCancellationRequested ? ScanStatus.Cancelled
            : ScanStatus.Succeeded;
        var completedAt = DateTimeOffset.UtcNow;
        var reason = result.FailureReason ?? (status == ScanStatus.Cancelled ? "Scan cancellation requested." : null);

        if (status == ScanStatus.Cancelled && cancellationOutcome?.Invoke() is { } outcome)
        {
            status = outcome.Status;
            reason = outcome.Reason + (result.FailureReason is null ? "" : " " + result.FailureReason);
        }

        // A failed finding write must not be retried as part of finalization.
        foreach (var entry in db.ChangeTracker.Entries<Finding>().Where(x => x.State == EntityState.Added).ToList())
        {
            execution.Findings.Remove(entry.Entity);
            entry.State = EntityState.Detached;
        }

        try
        {
            using var finalWrite = new CancellationTokenSource(WriteTimeout);
            var updated = await db.ScanExecutions
                .Where(x => x.Id == execution.Id && x.Status == ScanStatus.Running)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.NucleiVersion, execution.NucleiVersion)
                    .SetProperty(x => x.Status, status)
                    .SetProperty(x => x.CompletedAt, completedAt)
                    .SetProperty(x => x.ExitCode, result.ExitCode)
                    .SetProperty(x => x.StandardError, result.StandardError)
                    .SetProperty(x => x.FailureReason, reason), finalWrite.Token);
            if (updated != 1)
                throw new ScanPersistenceException($"Execution {execution.Id} could not be finalized because it is no longer Running or was removed. No terminal state was overwritten.");
        }
        catch (ScanPersistenceException) { throw; }
        catch
        {
            throw new ScanPersistenceException($"Could not confirm final status {status} was saved for execution {execution.Id}. Previously saved findings remain; the execution may still be Running. Inspect it when the database is available.");
        }

        execution.Status = status;
        execution.CompletedAt = completedAt;
        execution.ExitCode = result.ExitCode;
        execution.StandardError = result.StandardError;
        execution.FailureReason = reason;
        // ExecuteUpdate bypasses tracking. Accept the persisted values as the new
        // snapshot so marking this entry Unchanged cannot restore Running values.
        var executionEntry = db.Entry(execution);
        executionEntry.OriginalValues.SetValues(execution);
        executionEntry.State = EntityState.Unchanged;
        return execution;
    }
}
