namespace IsotopeProbe.Comparisons;

public sealed record FindingKey(string TemplateId, string MatchedAt, string? MatcherName);
public sealed record ComparisonFinding(int Id, string TemplateId, string MatchedAt, string? MatcherName, string Severity);
public enum ComparisonCategory { New, Both, NoLonger, IncompleteOlder, IncompleteNewer }
public sealed record ComparisonItem(FindingKey? Key, IReadOnlyList<ComparisonFinding> Older, IReadOnlyList<ComparisonFinding> Newer);
public sealed record ComparisonCounts(int New, int Both, int NoLonger, int IncompleteOlder, int IncompleteNewer,
    int OlderRecords, int NewerRecords, int MatcherAvailabilityWarnings);
public sealed record FindingComparisonResult(ComparisonCounts Counts, IReadOnlyDictionary<ComparisonCategory, IReadOnlyList<ComparisonItem>> Categories);

// The only identity policy: records use ordinal, case-sensitive string equality.
public static class FindingComparison
{
    public static FindingKey? Key(ComparisonFinding finding) =>
        string.IsNullOrWhiteSpace(finding.TemplateId) || string.IsNullOrWhiteSpace(finding.MatchedAt) ? null :
        new(finding.TemplateId, finding.MatchedAt, string.IsNullOrWhiteSpace(finding.MatcherName) ? null : finding.MatcherName);

    public static FindingComparisonResult Compare(IReadOnlyList<ComparisonFinding> older, IReadOnlyList<ComparisonFinding> newer,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var categories = Enum.GetValues<ComparisonCategory>().ToDictionary(x => x, _ => new List<ComparisonItem>());
        Dictionary<FindingKey, List<ComparisonFinding>> Group(IReadOnlyList<ComparisonFinding> records, bool baseline)
        {
            var groups = new Dictionary<FindingKey, List<ComparisonFinding>>();
            foreach (var record in records)
            {
                token.ThrowIfCancellationRequested();
                var key = Key(record);
                if (key is null)
                    categories[baseline ? ComparisonCategory.IncompleteOlder : ComparisonCategory.IncompleteNewer]
                        .Add(new(null, baseline ? [record] : [], baseline ? [] : [record]));
                else
                {
                    if (!groups.TryGetValue(key, out var occurrences)) groups[key] = occurrences = [];
                    occurrences.Add(record);
                }
            }
            return groups;
        }
        var left = Group(older, true);
        var right = Group(newer, false);
        foreach (var key in left.Keys.Union(right.Keys).OrderBy(x => x.TemplateId, StringComparer.Ordinal)
            .ThenBy(x => x.MatchedAt, StringComparer.Ordinal).ThenBy(x => x.MatcherName, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            left.TryGetValue(key, out var before); right.TryGetValue(key, out var after);
            var category = before is null ? ComparisonCategory.New : after is null ? ComparisonCategory.NoLonger : ComparisonCategory.Both;
            categories[category].Add(new(key, before ?? [], after ?? []));
        }
        var leftAvailability = left.Keys.GroupBy(x => (x.TemplateId, x.MatchedAt))
            .ToDictionary(g => g.Key, g => (Absent: g.Any(x => x.MatcherName is null), Named: g.Any(x => x.MatcherName is not null)));
        var warnings = 0;
        foreach (var group in right.Keys.GroupBy(x => (x.TemplateId, x.MatchedAt)))
        {
            token.ThrowIfCancellationRequested();
            var newerAvailability = (Absent: group.Any(x => x.MatcherName is null), Named: group.Any(x => x.MatcherName is not null));
            if (leftAvailability.TryGetValue(group.Key, out var availability) && availability != newerAvailability) warnings++;
        }
        return new(new(categories[ComparisonCategory.New].Count, categories[ComparisonCategory.Both].Count,
            categories[ComparisonCategory.NoLonger].Count, categories[ComparisonCategory.IncompleteOlder].Count,
            categories[ComparisonCategory.IncompleteNewer].Count, older.Count, newer.Count, warnings),
            categories.ToDictionary(x => x.Key, x => (IReadOnlyList<ComparisonItem>)x.Value));
    }
}
