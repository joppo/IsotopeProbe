using IsotopeProbe.Queue;
using IsotopeProbe.Queries;
using IsotopeProbe.Web.Scanning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IsotopeProbe.Web.Pages.Executions;

[Authorize]
public sealed class NewModel(OwnedScanSubmissionService submissions, ScanUser user,
    WebScanOptions options, SubmissionTokens tokens, IsotopeProbe.Profiles.ProfileCatalog profiles) : PageModel
{
    public IReadOnlyList<AllowedScanTarget> Targets => options.Targets;
    public IReadOnlyList<(string Id, string Name, string Description, int? Count, string? Error)> Profiles => profiles.Definitions.Select(p =>
    {
        try { return (p.Id, p.Name, p.Description, (int?)profiles.GetPrepared(p.Id).Manifest.Templates.Length, (string?)null); }
        catch (ArgumentException e) { return (p.Id, p.Name, p.Description, (int?)null, e.Message); }
    }).ToList();
    [BindProperty] public string ProfileId { get; set; } = "";
    [BindProperty] public string TargetId { get; set; } = "";
    [BindProperty] public string SubmissionToken { get; set; } = "";

    public void OnGet()
    {
        SubmissionToken = tokens.Create(user.Id);
        if (Profiles.Any(x => x.Id == "standard-website" && x.Error is null)) ProfileId = "standard-website";
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        try
        {
            var nonce = tokens.Validate(SubmissionToken, user.Id);
            var id = await submissions.SubmitAsync(TargetId, nonce, HttpContext.RequestAborted, ProfileId);
            return RedirectToPage("/Executions/Details", new { id });
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError("", exception.Message);
            return Page();
        }
    }
}
