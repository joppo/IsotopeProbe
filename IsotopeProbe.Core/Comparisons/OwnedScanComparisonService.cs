using IsotopeProbe.Domain;
using IsotopeProbe.Persistence;
using IsotopeProbe.Queries;
using Microsoft.EntityFrameworkCore;

namespace IsotopeProbe.Comparisons;

public sealed record ComparisonExecution(int Id, int? TargetId, string Url, DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt, ScanStatus Status, string? TemplatePath, string? TemplateProfile, int? TimeoutSeconds, string? ProfileId = null, string? ProfileVersion = null, string? SnapshotHash = null, string? NucleiVersion = null);
public sealed record ComparisonPage(ComparisonExecution Older, ComparisonExecution Newer, ComparisonCounts Counts,
    IReadOnlyList<string> Warnings, ComparisonCategory Category, Page<ComparisonItem> Items);
public sealed record ComparisonEvidence(ComparisonExecution Older, ComparisonExecution Newer, bool Baseline,
    Page<ComparisonFinding> Records);

public sealed class OwnedScanComparisonService(IsotopeProbeDbContext db, ScanUser user)
{
    // At most 50,000 lightweight rows per request; never silently truncate a comparison.
    public const int MaximumRecordsPerExecution = 25_000;
    private IQueryable<ScanExecution> Owned => db.ScanExecutions.AsNoTracking().Where(x => x.OwnerUserId == user.Id);
    private static IQueryable<ComparisonExecution> Project(IQueryable<ScanExecution> scans) => scans.Select(x =>
        new ComparisonExecution(x.Id, x.TargetId, x.Target, x.StartedAt, x.CompletedAt, x.Status,
            x.TemplatePath, x.TemplateProfile, x.TimeoutSeconds, x.ProfileId, x.ProfileVersion, x.SnapshotHash, x.NucleiVersion));
    private Task<ComparisonExecution?> Get(int id, CancellationToken token) => Project(Owned.Where(x => x.Id == id)).SingleOrDefaultAsync(token);
    private static void RequireSuccessful(ComparisonExecution scan)
    {
        if (scan.Status != ScanStatus.Succeeded || scan.CompletedAt is null)
            throw new ArgumentException("Comparison requires two completed successful executions.");
        if (scan.TargetId is null) throw new ArgumentException("Comparison requires a linked target.");
    }
    private async Task<(ComparisonExecution Older, ComparisonExecution Newer)?> Pair(int olderId, int newerId, CancellationToken token)
    {
        // Read both independently before eligibility checks to avoid exposing inaccessible rows.
        var older = await Get(olderId, token);
        var newer = await Get(newerId, token);
        if (older is null || newer is null) return null;
        if (olderId == newerId) throw new ArgumentException("Choose two different executions.");
        RequireSuccessful(older); RequireSuccessful(newer);
        if (older.TargetId != newer.TargetId) throw new ArgumentException("Both executions must belong to the same target.");
        return (older, newer);
    }
    public async Task<Page<ComparisonExecution>?> BaselinesAsync(int newerId, int skip = 0, int take = 20, CancellationToken token = default)
    {
        OwnedScanQueryService.ValidatePagination(skip, take);
        var current = await Get(newerId, token);
        if (current is null) return null;
        RequireSuccessful(current);
        var candidates = Owned.Where(x => x.Id != newerId && x.TargetId == current.TargetId &&
            x.Status == ScanStatus.Succeeded && x.CompletedAt != null);
        var count = await candidates.CountAsync(token);
        var items = await Project(candidates.OrderByDescending(x => x.StartedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(x => x.Id).Skip(skip).Take(take)).ToListAsync(token);
        return new(items, count, skip, take);
    }
    public async Task<int?> PreviousSuccessfulAsync(int newerId, CancellationToken token = default)
    {
        var current = await Get(newerId, token);
        if (current is null || current.Status != ScanStatus.Succeeded || current.CompletedAt is null ||
            current.TargetId is null || current.StartedAt is null) return null;
        return await Owned.Where(x => x.TargetId == current.TargetId && x.Status == ScanStatus.Succeeded && x.CompletedAt != null &&
                (x.StartedAt < current.StartedAt || (x.StartedAt == current.StartedAt && x.Id < newerId)))
            .OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id).Select(x => (int?)x.Id).FirstOrDefaultAsync(token);
    }
    private async Task<List<ComparisonFinding>> Findings(int scanId, CancellationToken token)
    {
        var records = await db.Findings.AsNoTracking().Where(x => x.ScanExecutionId == scanId && x.ScanExecution.OwnerUserId == user.Id)
            .OrderBy(x => x.Id).Select(x => new ComparisonFinding(x.Id, x.TemplateId, x.MatchedAt, x.MatcherName, x.Severity))
            .Take(MaximumRecordsPerExecution + 1).ToListAsync(token);
        if (records.Count > MaximumRecordsPerExecution)
            throw new ArgumentException($"Comparison supports at most {MaximumRecordsPerExecution:N0} finding records per execution. This pair was not compared.");
        return records;
    }
    public async Task<ComparisonPage?> CompareAsync(int olderId, int newerId, ComparisonCategory category = ComparisonCategory.New,
        int skip = 0, int take = 20, CancellationToken token = default)
    {
        OwnedScanQueryService.ValidatePagination(skip, take);
        if (!Enum.IsDefined(category)) throw new ArgumentException("Choose a valid comparison category.");
        var pair = await Pair(olderId, newerId, token);
        if (pair is null) return null;
        var (older, newer) = pair.Value;
        var result = FindingComparison.Compare(await Findings(olderId, token), await Findings(newerId, token), token);
        var warnings = new List<string>
        {
            ProvenanceWarning(older, newer),
            "Successful status does not prove identical coverage or that every check succeeded. These are differences in detected findings; no longer detected does not mean fixed."
        };
        if (older.Url != newer.Url || older.TemplatePath != newer.TemplatePath || older.TemplateProfile != newer.TemplateProfile || older.TimeoutSeconds != newer.TimeoutSeconds)
            warnings.Add("Recorded scan configuration differs between these executions.");
        if (older.StartedAt is null || newer.StartedAt is null || older.StartedAt > newer.StartedAt ||
            (older.StartedAt == newer.StartedAt && older.Id > newer.Id))
            warnings.Add("The selected baseline is not known to precede the newer execution. Category direction follows your baseline/newer selection.");
        if (result.Counts.MatcherAvailabilityWarnings > 0)
            warnings.Add($"Matcher-name availability differs at {result.Counts.MatcherAvailabilityWarnings} template/location pair(s). New/no-longer-detected items may reflect missing historical metadata rather than a changed security condition.");
        var items = result.Categories[category];
        return new(older, newer, result.Counts, warnings, category, new(items.Skip(skip).Take(take).ToList(), items.Count, skip, take));
    }
    public static string ProvenanceWarning(ComparisonExecution older, ComparisonExecution newer)
    {
        var differences = new List<string>();
        if (older.ProfileId is null || newer.ProfileId is null || older.ProfileVersion is null || newer.ProfileVersion is null ||
            older.SnapshotHash is null || newer.SnapshotHash is null || older.NucleiVersion is null || newer.NucleiVersion is null)
            differences.Add("Coverage cannot be established: provenance is unknown or incomplete for at least one execution; matching names or paths do not establish coverage.");
        if (older.ProfileId != newer.ProfileId || older.ProfileVersion != newer.ProfileVersion) differences.Add("Profile IDs or versions differ.");
        if (older.SnapshotHash != newer.SnapshotHash) differences.Add("Template snapshot hashes differ.");
        if (older.NucleiVersion != newer.NucleiVersion) differences.Add("Nuclei versions differ.");
        return differences.Count == 0 ? "Matching recorded template content and engine version; identical runtime coverage is not guaranteed."
            : string.Join(" ", differences);
    }
    public async Task<ComparisonEvidence?> EvidenceAsync(int olderId, int newerId, bool baseline, int representativeId,
        int skip = 0, int take = 20, CancellationToken token = default)
    {
        OwnedScanQueryService.ValidatePagination(skip, take);
        var pair = await Pair(olderId, newerId, token);
        if (pair is null) return null;
        var records = await Findings(baseline ? olderId : newerId, token);
        var representative = records.SingleOrDefault(x => x.Id == representativeId);
        if (representative is null) return null;
        var key = FindingComparison.Key(representative);
        var matching = records.Where(x => key is null ? x.Id == representativeId : FindingComparison.Key(x) == key).ToList();
        token.ThrowIfCancellationRequested();
        return new(pair.Value.Older, pair.Value.Newer, baseline, new(matching.Skip(skip).Take(take).ToList(), matching.Count, skip, take));
    }
}
