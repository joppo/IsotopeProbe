using IsotopeProbe.Web.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace IsotopeProbe.Web.Pages.Account;

[AllowAnonymous]
public sealed class LoginModel : PageModel
{
    public string ReturnUrl { get; private set; } = "/";
    public void OnGet(string? returnUrl) => ReturnUrl = GoogleSession.LocalReturnUrl(returnUrl);
    public IActionResult OnPost(string? returnUrl) => Challenge(
        new AuthenticationProperties { RedirectUri = GoogleSession.LocalReturnUrl(returnUrl) },
        GoogleDefaults.AuthenticationScheme);
}
