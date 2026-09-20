using IsotopeProbe.Comparisons;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IsotopeProbe.Web.Pages.Comparisons;

public sealed class EvidenceModel(OwnedScanComparisonService comparisons) : PageModel
{
    public ComparisonEvidence Evidence { get; private set; } = null!;
    public int RepresentativeId { get; private set; }
    public async Task<IActionResult> OnGetAsync(int olderId, int newerId, bool baseline, int representativeId, int skip = 0)
    {
        if (!ModelState.IsValid || skip < 0) return BadRequest();
        try
        {
            var evidence = await comparisons.EvidenceAsync(olderId, newerId, baseline, representativeId, skip, token: HttpContext.RequestAborted);
            if (evidence is null) return NotFound();
            Evidence = evidence; RepresentativeId = representativeId;
            return Page();
        }
        catch (ArgumentException exception) { return BadRequest(exception.Message); }
    }
}
