using IsotopeProbe.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IsotopeProbe.Targets;

// Identity/history only: this service does not grant permission to scan a URL.
public sealed class TargetResolver(IsotopeProbeDbContext db)
{
    // Conservative shared SQL rule for legacy inputs: absolute HTTP(S), a host,
    // optional numeric port, no credentials or whitespace. Preserve the exact string.
    // Keep the frozen backfill predicate in AddPersistentTargets consistent.
    internal const string UsableUrlPattern = @"^https?://([a-z0-9]([a-z0-9.-]*[a-z0-9])?|\[[0-9a-f:.]+\])(:[0-9]+)?([/?#][^[:space:]]*)?$";

    public async Task<int> ResolveAsync(Guid owner, string url, string? name = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("A target URL is required.");
        // Preserve accepted CLI inputs too (Nuclei can accept a host without a scheme).
        // DO NOTHING waits for a concurrent insert; the subsequent READ COMMITTED
        // statement sees the winning row. Also participates in callers' transactions.
        var label = string.IsNullOrWhiteSpace(name) ? url : name;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO targets ("OwnerUserId", "Name", "Url", "CreatedAtUtc")
            VALUES ({owner}, {label}, {url}, CURRENT_TIMESTAMP)
            ON CONFLICT ("OwnerUserId", "Url") DO NOTHING
            """, token);
        return await db.Targets.Where(x => x.OwnerUserId == owner && x.Url == url)
            .Select(x => x.Id).SingleAsync(token);
    }
    public async Task<int?> ResolveLegacyAsync(Guid owner, string url, CancellationToken token = default)
    {
        var usable = await db.Database.SqlQuery<bool>($"SELECT {url} ~* {UsableUrlPattern} AS \"Value\"").SingleAsync(token);
        return usable ? await ResolveAsync(owner, url, token: token) : null;
    }
}
