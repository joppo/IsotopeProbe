using IsotopeProbe.Domain;
using IsotopeProbe.Identity;
using IsotopeProbe.Persistence;
using IsotopeProbe.Queries;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace IsotopeProbe.Web.Pages.Account;

public sealed class IndexModel(IsotopeProbeDbContext db, GroupService groups, ScanUser currentUser) : PageModel
{
    public User Profile { get; private set; } = null!;
    public List<Group> Memberships { get; private set; } = [];
    public async Task OnGetAsync()
    {
        Profile = await db.Users.AsNoTracking().SingleAsync(x => x.Id == currentUser.Id, HttpContext.RequestAborted);
        Memberships = await groups.MembershipsAsync(currentUser.Id, HttpContext.RequestAborted);
    }
}
