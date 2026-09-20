using IsotopeProbe.Queries;
using IsotopeProbe.Queue;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IsotopeProbe.Web.Pages.Targets;

public sealed class DetailsModel(OwnedTargetQueryService queries, WebScanOptions options) : PageModel
{
    public TargetSummary Target { get; private set; } = null!;
    public Page<ScanSummary> History { get; private set; } = null!;
    public bool IsAllowed => options.Targets.Any(x => string.Equals(x.Url, Target.Url, StringComparison.Ordinal));
    public async Task<IActionResult> OnGetAsync(int id, int skip = 0)
    {
        if (!ModelState.IsValid || skip < 0) return BadRequest();
        if (id <= 0) return NotFound();
        var target = await queries.GetAsync(id, HttpContext.RequestAborted);
        if (target is null) return NotFound();
        Target = target;
        var history = await queries.HistoryAsync(id, skip, token: HttpContext.RequestAborted);
        if (history is null) return NotFound();
        History = history;
        return Page();
    }
}
