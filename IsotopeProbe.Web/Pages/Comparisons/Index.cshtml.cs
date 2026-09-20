using IsotopeProbe.Comparisons;
using IsotopeProbe.Queries;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IsotopeProbe.Web.Pages.Comparisons;

public sealed class IndexModel(OwnedScanComparisonService comparisons) : PageModel
{
    public int NewerId { get; private set; }
    public int? OlderId { get; private set; }
    public string? Error { get; private set; }
    public ComparisonPage? Comparison { get; private set; }
    public Page<ComparisonExecution>? Baselines { get; private set; }
    public async Task<IActionResult> OnGetAsync(int newerId, int? olderId, ComparisonCategory category = ComparisonCategory.New,
        int skip = 0, int baselineSkip = 0)
    {
        if (!ModelState.IsValid || skip < 0 || baselineSkip < 0 || !Enum.IsDefined(category)) return BadRequest();
        NewerId = newerId;
        try
        {
            OlderId = olderId ?? await comparisons.PreviousSuccessfulAsync(newerId, HttpContext.RequestAborted);
            if (OlderId is int baseline)
            {
                Comparison = await comparisons.CompareAsync(baseline, newerId, category, skip, token: HttpContext.RequestAborted);
                if (Comparison is null) return NotFound();
            }
            Baselines = await comparisons.BaselinesAsync(newerId, baselineSkip, token: HttpContext.RequestAborted);
            if (Baselines is null) return NotFound();
        }
        catch (ArgumentException exception) { Error = exception.Message; }
        return Page();
    }
    public static string Label(ComparisonCategory category) => category switch
    {
        ComparisonCategory.New => "Newly detected",
        ComparisonCategory.Both => "Detected in both",
        ComparisonCategory.NoLonger => "No longer detected",
        ComparisonCategory.IncompleteOlder => "Excluded baseline records",
        _ => "Excluded newer records"
    };
    public static string Severities(IReadOnlyList<ComparisonFinding> records) => records.Count == 0 ? "—" :
        string.Join(", ", records.Select(x => x.Severity).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
}
