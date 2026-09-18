using System.Text.Json;
using IsotopeProbe.Queries;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IsotopeProbe.Web.Pages.Findings;

public sealed class DetailsModel(OwnedScanQueryService queries) : PageModel
{
    public FindingDetails Finding { get; private set; } = null!;
    public string? Description { get; private set; }
    public string? ExtractedResults { get; private set; }
    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!ModelState.IsValid) return BadRequest();
        if (id <= 0) return NotFound();
        var finding = await queries.GetFindingAsync(id, HttpContext.RequestAborted);
        if (finding is null) return NotFound();
        Finding = finding;
        // These Nuclei fields are retained in RawJson, rather than separate columns.
        using var json = JsonDocument.Parse(finding.RawJson);
        if (json.RootElement.ValueKind == JsonValueKind.Object)
        {
            if (json.RootElement.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("description", out var description))
                Description = description.ToString();
            if (json.RootElement.TryGetProperty("extracted-results", out var extracted))
                ExtractedResults = extracted.ToString();
        }
        return Page();
    }
}
