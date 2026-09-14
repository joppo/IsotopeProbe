using IsotopeProbe.Persistence;
using IsotopeProbe.Queries;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();
builder.Services.AddDbContext<IsotopeProbeDbContext>(options =>
{
    var connectionString = Environment.GetEnvironmentVariable("ISOTOPEPROBE_CONNECTION_STRING");
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("Database configuration is missing.");
    options.UseNpgsql(connectionString);
});
builder.Services.AddScoped<ScanQueryService>();
var app = builder.Build();
// Use a safe error page in Development too: database exceptions may contain secrets.
app.UseExceptionHandler("/Error");
app.UseStatusCodePages("text/plain", "Request could not be completed (HTTP {0}).");
app.UseStaticFiles();
app.MapRazorPages();
app.Run();
