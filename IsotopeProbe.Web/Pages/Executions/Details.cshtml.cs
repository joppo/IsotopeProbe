using IsotopeProbe.Queries;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IsotopeProbe.Web.Pages.Executions;

public sealed class DetailsModel(OwnedScanQueryService queries) : PageModel
{
    public ScanDetails Scan { get; private set; } = null!;
    public Page<FindingSummary> Findings { get; private set; } = null!;
    public string? Severity { get; private set; }
    public async Task<IActionResult> OnGetAsync(int id, int skip = 0, string? severity = null)
    {
        if (!ModelState.IsValid || skip < 0) return BadRequest();
        if (id <= 0) return NotFound();
        var scan = await queries.GetScanAsync(id, HttpContext.RequestAborted);
        if (scan is null) return NotFound();
        Scan = scan;
        Severity = string.IsNullOrEmpty(severity) ? null : severity;
        var findings = await queries.ListFindingsAsync(id, skip, cancellationToken: HttpContext.RequestAborted, severity: Severity);
        if (findings is null) return NotFound();
        Findings = findings;
        return Page();
    }
}
