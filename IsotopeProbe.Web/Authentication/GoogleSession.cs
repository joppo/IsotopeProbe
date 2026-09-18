using System.Security.Claims;
using IsotopeProbe.Identity;
using IsotopeProbe.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.EntityFrameworkCore;

namespace IsotopeProbe.Web.Authentication;

public static class GoogleSession
{
    public static async Task CompleteAsync(OAuthCreatingTicketContext context)
    {
        var subject = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Google did not supply a subject.");
        var users = context.HttpContext.RequestServices.GetRequiredService<UserService>();
        var user = await users.ResolveGoogleAsync(subject,
            context.Principal?.FindFirstValue(ClaimTypes.Email),
            context.Principal?.FindFirstValue(ClaimTypes.Name), context.HttpContext.RequestAborted);
        // Replace external claims: only our internal identity enters the application cookie.
        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], CookieAuthenticationDefaults.AuthenticationScheme));
        context.Properties.IsPersistent = false;
        context.Properties.RedirectUri = LocalReturnUrl(context.Properties.RedirectUri);
    }

    public static string LocalReturnUrl(string? value) =>
        Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult.IsLocalUrl(value) ? value! : "/";

    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var value = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(value, out var id) || id == Guid.Empty ||
            !await context.HttpContext.RequestServices.GetRequiredService<IsotopeProbeDbContext>()
                .Users.AsNoTracking().AnyAsync(x => x.Id == id, context.HttpContext.RequestAborted))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}
