using IsotopeProbe.Domain;
using IsotopeProbe.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IsotopeProbe.Identity;

public sealed class UserService(IsotopeProbeDbContext db)
{
    public const string GoogleProvider = "https://accounts.google.com";

    // Called only after the external handler has validated the Google identity.
    public async Task<User> ResolveGoogleAsync(string subject, string? email, string? name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 255)
            throw new ArgumentException("A valid Google subject is required.");
        var login = await db.ExternalLogins.Include(x => x.User)
            .SingleOrDefaultAsync(x => x.Provider == GoogleProvider && x.Subject == subject, cancellationToken);
        if (login is not null)
        {
            login.User.Email = email;
            login.User.DisplayName = name;
            await db.SaveChangesAsync(cancellationToken);
            return login.User;
        }

        var user = new User { Email = email, DisplayName = name };
        login = new ExternalLogin { Provider = GoogleProvider, Subject = subject, User = user };
        // The user and login insert commit together; a losing callback leaves no orphan user.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.ExternalLogins.Add(login);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return user;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken);
            db.Entry(login).State = EntityState.Detached;
            db.Entry(user).State = EntityState.Detached;
            return await db.ExternalLogins.Where(x => x.Provider == GoogleProvider && x.Subject == subject)
                .Select(x => x.User).SingleAsync(cancellationToken);
        }
    }

    public async Task RequireUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!await db.Users.AnyAsync(x => x.Id == userId, cancellationToken))
            throw new ArgumentException("The internal user does not exist.");
    }

    public async Task AssignUnownedAsync(Guid userId, IReadOnlyCollection<int> executionIds,
        CancellationToken cancellationToken = default)
    {
        await RequireUserAsync(userId, cancellationToken);
        if (executionIds.Count == 0 || executionIds.Any(x => x <= 0) || executionIds.Distinct().Count() != executionIds.Count)
            throw new ArgumentException("Supply distinct positive execution IDs.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var updated = await db.ScanExecutions.Where(x => executionIds.Contains(x.Id) && x.OwnerUserId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.OwnerUserId, userId), cancellationToken);
        if (updated != executionIds.Count)
            throw new ArgumentException("An execution is missing or already owned. No ownership was changed.");
        var assigned = await db.ScanExecutions.AsNoTracking().Where(x => executionIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Target }).ToListAsync(cancellationToken);
        // Resolve URLs in a stable order to avoid inverse lock ordering between assignments.
        foreach (var group in assigned.GroupBy(x => x.Target).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var targetId = await new Targets.TargetResolver(db).ResolveLegacyAsync(userId, group.Key, cancellationToken);
            var ids = group.Select(x => x.Id).ToArray();
            await db.ScanExecutions.Where(x => ids.Contains(x.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.TargetId, targetId), cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
