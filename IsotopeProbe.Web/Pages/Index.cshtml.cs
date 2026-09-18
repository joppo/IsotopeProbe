using IsotopeProbe.Queries;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IsotopeProbe.Web.Pages;

public sealed class IndexModel(OwnedScanQueryService queries) : PageModel
{
    public Page<ScanSummary> Scans { get; private set; } = null!;
    public async Task<IActionResult> OnGetAsync(int skip = 0)
    {
        if (!ModelState.IsValid || skip < 0) return BadRequest();
        Scans = await queries.ListScansAsync(skip, cancellationToken: HttpContext.RequestAborted);
        return Page();
    }
}
