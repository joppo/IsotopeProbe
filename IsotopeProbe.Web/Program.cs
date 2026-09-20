using IsotopeProbe;
using IsotopeProbe.Nuclei;
using IsotopeProbe.Queue;
using IsotopeProbe.Web.Scanning;
using System.Security.Claims;
using IsotopeProbe.Identity;
using IsotopeProbe.Persistence;
using IsotopeProbe.Queries;
using IsotopeProbe.Web.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
string Required(string key) => !string.IsNullOrWhiteSpace(builder.Configuration[key])
    ? builder.Configuration[key]! : throw new InvalidOperationException($"Required configuration is missing: {key}.");
builder.Services.AddRazorPages(options => options.Conventions.AllowAnonymousToPage("/Error"));
builder.Services.AddDbContext<IsotopeProbeDbContext>(options => options.UseNpgsql(Required("ISOTOPEPROBE_CONNECTION_STRING")));
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<GroupService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped(sp =>
{
    var principal = sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.User;
    if (principal?.Identity?.IsAuthenticated != true ||
        !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
        throw new InvalidOperationException("A validated local session is required.");
    return new ScanUser(id);
});
builder.Services.AddScoped<OwnedScanQueryService>();
builder.Services.AddScoped<OwnedTargetQueryService>();
builder.Services.AddScoped<IsotopeProbe.Comparisons.OwnedScanComparisonService>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "__Host-IsotopeProbe";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;
        options.Events.OnValidatePrincipal = GoogleSession.ValidateAsync;
    })
    .AddGoogle(options =>
    {
        options.ClientId = Required("Authentication:Google:ClientId");
        options.ClientSecret = Required("Authentication:Google:ClientSecret");
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.CallbackPath = "/signin-google";
        options.SaveTokens = false;
        options.UsePkce = true;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        // Keep framework correlation/state protection and its SameSite=None secure cookie.
        options.Events.OnCreatingTicket = GoogleSession.CompleteAsync;
        options.Events.OnRemoteFailure = context =>
        {
            context.HandleResponse();
            context.Response.Redirect("/Account/SignInFailure");
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(options => options.FallbackPolicy =
    new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddAntiforgery(options => options.Cookie.SecurePolicy = CookieSecurePolicy.Always);
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IConfiguration>().GetSection("WebScans").Get<WebScanOptions>() ?? new();
    options.Validate();
    return options;
});
builder.Services.AddSingleton(_ => IsotopeProbe.Profiles.ProfileCatalog.FromEnvironment());
builder.Services.AddSingleton<SubmissionTokens>();
builder.Services.AddScoped<OwnedScanSubmissionService>();
builder.Services.AddScoped<NucleiFindingParser>();
builder.Services.AddScoped(sp => new NucleiRunner(sp.GetRequiredService<NucleiFindingParser>(),
    sp.GetRequiredService<WebScanOptions>().ExecutablePath));
builder.Services.AddScoped<ScanService>();
builder.Services.AddScoped<WebScanDispatcher>();
builder.Services.AddHostedService<ScanWorker>();
// Finding write (10s), process cleanup (10s), final write (10s), plus margin.
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(40));
var app = builder.Build();
app.Services.GetRequiredService<WebScanOptions>();
app.Services.GetRequiredService<IsotopeProbe.Profiles.ProfileCatalog>();
// Validate before accepting requests, after all host configuration sources are applied.
Required("ISOTOPEPROBE_CONNECTION_STRING");
Required("Authentication:Google:ClientId");
Required("Authentication:Google:ClientSecret");
// Generic errors in Development too; never render database/authentication exceptions.
app.UseExceptionHandler("/Error");
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseHttpsRedirection();
app.UseStatusCodePages("text/plain", "Request could not be completed (HTTP {0}).");
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.Run();

public partial class Program { }
