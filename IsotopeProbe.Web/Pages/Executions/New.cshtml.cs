using IsotopeProbe.Queue;
using IsotopeProbe.Queries;
using IsotopeProbe.Web.Scanning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IsotopeProbe.Web.Pages.Executions;

[Authorize]
public sealed class NewModel(OwnedScanSubmissionService submissions, ScanUser user,
    WebScanOptions options, SubmissionTokens tokens) : PageModel
{
    public IReadOnlyList<AllowedScanTarget> Targets => options.Targets;
    public string Profile => options.TemplateProfile;
    [BindProperty] public string TargetId { get; set; } = "";
    [BindProperty] public string SubmissionToken { get; set; } = "";

    public void OnGet() => SubmissionToken = tokens.Create(user.Id);

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        try
        {
            var nonce = tokens.Validate(SubmissionToken, user.Id);
            var id = await submissions.SubmitAsync(TargetId, nonce, HttpContext.RequestAborted);
            return RedirectToPage("/Executions/Details", new { id });
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError("", exception.Message);
            return Page();
        }
    }
}
