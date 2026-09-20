using IsotopeProbe.Domain;
using IsotopeProbe.Identity;
using IsotopeProbe.Persistence;
using IsotopeProbe.Queries;
using Microsoft.EntityFrameworkCore;

namespace IsotopeProbe.Queue;

// Returns only the current user's submission ID; no unrestricted read or claim API.
public sealed class OwnedScanSubmissionService(IsotopeProbeDbContext db, ScanUser user, WebScanOptions options, IsotopeProbe.Profiles.ProfileCatalog profiles)
{
    public async Task<int> SubmitAsync(string targetId, Guid submissionId, CancellationToken token = default, string? profileId = null)
    {
        if (submissionId == Guid.Empty) throw new ArgumentException("Invalid submission token. Open New scan again.");
        options.Validate();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        // All admissions serialize on this transaction-scoped PostgreSQL lock. Under
        // READ COMMITTED each subsequent count sees the previous admission's commit.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(724196381)", token);
        var existing = await db.ScanExecutions.Where(x => x.OwnerUserId == user.Id && x.SubmissionId == submissionId)
            .Select(x => (int?)x.Id).SingleOrDefaultAsync(token);
        if (existing.HasValue) return existing.Value;
        var target = options.Targets.SingleOrDefault(x => string.Equals(x.Id, targetId, StringComparison.Ordinal))
            ?? throw new ArgumentException("Choose an allowed target from the list.");
        await new UserService(db).RequireUserAsync(user.Id, token);
        var web = db.ScanExecutions.Where(x => x.Source == ScanSource.Web);
        if (await web.CountAsync(x => x.OwnerUserId == user.Id &&
            (x.Status == ScanStatus.Queued || x.Status == ScanStatus.Running), token) >= options.PerUserLimit)
            throw new ArgumentException("Your queued or running scan limit has been reached. Wait for completion and refresh.");
        if (await web.CountAsync(x => x.Status == ScanStatus.Queued, token) >= options.QueueLimit)
            throw new ArgumentException("The scan queue is full. Try again later.");
        var persistentTargetId = await new Targets.TargetResolver(db).ResolveAsync(user.Id, target.Url, target.Name, token);
        var execution = new ScanExecution
        {
            TargetId = persistentTargetId, Source = ScanSource.Web, Status = ScanStatus.Queued, OwnerUserId = user.Id,
            SubmissionId = submissionId, EnqueuedAt = DateTimeOffset.UtcNow,
            Target = target.Url, TimeoutSeconds = options.TimeoutSeconds
        };
        profiles.GetPrepared(profileId ?? "").Capture(execution);
        db.ScanExecutions.Add(execution);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return execution.Id;
    }
}
