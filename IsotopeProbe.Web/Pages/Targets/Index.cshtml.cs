using IsotopeProbe.Queries;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IsotopeProbe.Web.Pages.Targets;

public sealed class IndexModel(OwnedTargetQueryService queries) : PageModel
{
    public Page<TargetSummary> Targets { get; private set; } = null!;
    public async Task<IActionResult> OnGetAsync(int skip = 0)
    {
        if (!ModelState.IsValid || skip < 0) return BadRequest();
        Targets = await queries.ListAsync(skip, token: HttpContext.RequestAborted);
        return Page();
    }
}
