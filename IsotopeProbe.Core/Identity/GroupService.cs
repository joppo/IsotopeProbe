using IsotopeProbe.Domain;
using IsotopeProbe.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IsotopeProbe.Identity;

public sealed class GroupService(IsotopeProbeDbContext db)
{
    public async Task<Group> CreateAsync(string name, string? description = null, CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        var normalized = name.ToUpperInvariant();
        if (name.Length is < 1 or > 100 || normalized.Length > 100)
            throw new ArgumentException("Group names must contain 1–100 characters after trimming.");
        var group = new Group { Name = name, NormalizedName = normalized, Description = description };
        db.Groups.Add(group);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(group).State = EntityState.Detached;
            throw new ArgumentException("A group with that normalized name already exists.");
        }
        return group;
    }

    public async Task AddAsync(Guid userId, int groupId, CancellationToken cancellationToken = default)
    {
        await new UserService(db).RequireUserAsync(userId, cancellationToken);
        if (!await db.Groups.AnyAsync(x => x.Id == groupId, cancellationToken))
            throw new ArgumentException("The group does not exist.");
        // Database conflict handling makes repeated and concurrent additions idempotent.
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO user_groups (\"UserId\", \"GroupId\") VALUES ({userId}, {groupId}) ON CONFLICT DO NOTHING", cancellationToken);
    }

    public async Task RemoveAsync(Guid userId, int groupId, CancellationToken cancellationToken = default)
    {
        await new UserService(db).RequireUserAsync(userId, cancellationToken);
        if (!await db.Groups.AnyAsync(x => x.Id == groupId, cancellationToken))
            throw new ArgumentException("The group does not exist.");
        await db.UserGroups.Where(x => x.UserId == userId && x.GroupId == groupId).ExecuteDeleteAsync(cancellationToken);
    }

    public Task<List<Group>> MembershipsAsync(Guid userId, CancellationToken cancellationToken = default) =>
        db.UserGroups.AsNoTracking().Where(x => x.UserId == userId).Select(x => x.Group)
            .OrderBy(x => x.NormalizedName).ToListAsync(cancellationToken);
}
