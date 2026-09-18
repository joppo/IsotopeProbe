using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IsotopeProbe.Domain;
using IsotopeProbe.Identity;
using IsotopeProbe.Persistence;
using IsotopeProbe.Queries;
using IsotopeProbe.Web.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IsotopeProbe.Tests;

public sealed class ReturnUrlTests
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example/", "/")]
    [InlineData("/\nevil", "/")]
    [InlineData("/executions/1?skip=20", "/executions/1?skip=20")]
    public void OnlyLocalReturnUrls(string? input, string expected) => Assert.Equal(expected, GoogleSession.LocalReturnUrl(input));

    [Fact]
    public void OwnerOptionAndOperatorArgumentsAreValidated()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, Cli.ScanOptions.Parse(["--owner", id.ToString(), "http://localhost"]).OwnerUserId);
        Assert.Throws<ArgumentException>(() => Cli.ScanOptions.Parse(["http://localhost", "--owner", "bad"]));
        Assert.Throws<ArgumentException>(() => Cli.OperatorConsole.Validate(["scans", "assign", "--user", id.ToString(), "--executions"]));
        Assert.Throws<ArgumentException>(() => new ScanUser(Guid.Empty));
    }
}

public sealed class AuthenticationTests : IAsyncLifetime
{
    private readonly string schema = "auth_test_" + Guid.NewGuid().ToString("N");
    private NpgsqlConnection? admin;
    private string connectionString = "";
    private IsotopeProbeDbContext Context(params IInterceptor[] interceptors) => new(
        new DbContextOptionsBuilder<IsotopeProbeDbContext>().UseNpgsql(connectionString).AddInterceptors(interceptors).Options);

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ISOTOPEPROBE_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(configured)) return;
        admin = new NpgsqlConnection(configured);
        await admin.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin);
        await create.ExecuteNonQueryAsync();
        connectionString = new NpgsqlConnectionStringBuilder(configured) { SearchPath = schema, Pooling = false }.ConnectionString;
        await using var db = Context();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (admin is null) return;
        await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
        await drop.ExecuteNonQueryAsync();
        await admin.DisposeAsync();
    }

    [PostgresFact]
    public async Task Provisioning_UsesSubjectNeverEmail_AndConcurrentCallbacksLeaveNoOrphans()
    {
        await using var db = Context();
        var users = new UserService(db);
        var a = await users.ResolveGoogleAsync("a", "same@example.test", "First");
        var repeated = await users.ResolveGoogleAsync("a", "updated@example.test", "Updated");
        Assert.Equal(a.Id, repeated.Id);
        var b = await users.ResolveGoogleAsync("b", "updated@example.test", null);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Empty(await db.UserGroups.ToListAsync());
        Assert.Equal(0, (await new OwnedScanQueryService(db, new(a.Id)).ListScansAsync()).TotalCount);
        var barrier = new ProvisioningBarrier();
        await using var first = Context(barrier);
        await using var second = Context(barrier);
        var callbacks = await Task.WhenAll(
            new UserService(first).ResolveGoogleAsync("concurrent", null, null),
            new UserService(second).ResolveGoogleAsync("concurrent", null, null));
        Assert.Equal(callbacks[0].Id, callbacks[1].Id);
        Assert.Equal(3, await db.Users.CountAsync());
        Assert.Equal(3, await db.ExternalLogins.CountAsync());
    }

    [PostgresFact]
    public async Task OwnershipQueries_FilterBeforeCountsPagingAndSeverity_RegardlessOfGroups()
    {
        await using var db = Context();
        var a = await new UserService(db).ResolveGoogleAsync("a", null, null);
        var b = await new UserService(db).ResolveGoogleAsync("b", null, null);
        var groupService = new GroupService(db);
        var group = await groupService.CreateAsync(" Administrators ");
        await Assert.ThrowsAsync<ArgumentException>(() => groupService.CreateAsync("administrators"));
        await groupService.AddAsync(a.Id, group.Id);
        await groupService.AddAsync(a.Id, group.Id);
        await groupService.AddAsync(b.Id, group.Id);
        Assert.Equal(2, await db.UserGroups.CountAsync());
        var own = Scan(a.Id, "a", "high", "low", "high");
        var other = Scan(b.Id, "b", "critical");
        var historical = Scan(null, "old", "critical");
        db.AddRange(own, other, historical, Scan(a.Id, "a2", "info"));
        await db.SaveChangesAsync();
        var newcomer = await new UserService(db).ResolveGoogleAsync("newcomer", null, null);
        Assert.Equal(0, (await new OwnedScanQueryService(db, new(newcomer.Id)).ListScansAsync()).TotalCount);
        var queries = new OwnedScanQueryService(db, new(a.Id));
        Assert.Equal(2, (await queries.ListScansAsync(take: 1)).TotalCount);
        Assert.Single((await queries.ListScansAsync(skip: 1, take: 1)).Items);
        Assert.Empty((await queries.ListScansAsync(skip: 2)).Items);
        Assert.Null(await queries.GetScanAsync(other.Id));
        Assert.Null(await queries.GetScanAsync(historical.Id));
        Assert.Null(await queries.GetFindingAsync(other.Findings[0].Id));
        Assert.Null(await queries.GetFindingAsync(historical.Findings[0].Id));
        Assert.Null(await queries.ListFindingsAsync(other.Id, severity: "critical"));
        Assert.Null(await queries.ListFindingsAsync(historical.Id));
        var findings = (await queries.ListFindingsAsync(own.Id, skip: 1, take: 1, severity: "high"))!;
        Assert.Equal(2, findings.TotalCount);
        Assert.Single(findings.Items);
        var detail = (await queries.GetScanAsync(own.Id))!;
        Assert.Equal(3, detail.Execution.FindingCount);
        Assert.DoesNotContain(detail.Severities, x => x.Severity == "critical");
        Assert.NotNull(await queries.GetFindingAsync(own.Findings[0].Id));
        Assert.Equal(4, (await new TrustedScanQueryService(db).ListScansAsync()).TotalCount);
        // A multi-ID assignment is atomic even when one ID is already owned.
        var users = new UserService(db);
        await Assert.ThrowsAsync<ArgumentException>(() => users.AssignUnownedAsync(a.Id, [historical.Id, other.Id]));
        Assert.Null(await queries.GetScanAsync(historical.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => users.AssignUnownedAsync(Guid.NewGuid(), [historical.Id]));
        await users.AssignUnownedAsync(a.Id, [historical.Id]);
        Assert.NotNull(await queries.GetScanAsync(historical.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => users.AssignUnownedAsync(b.Id, [historical.Id]));
        await groupService.RemoveAsync(a.Id, group.Id);
        Assert.Empty(await groupService.MembershipsAsync(a.Id));
    }

    [PostgresFact]
    public async Task Web_RealCookieAndOAuthMiddlewareWithMockGoogle_EnforcesIsolationCsrfAndLogout()
    {
        await using var factory = new AuthWebFactory(connectionString);
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost:7080"), AllowAutoRedirect = false });
        foreach (var path in new[] { "/", "/executions/1", "/findings/1", "/Account" })
        {
            var anonymous = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
            Assert.StartsWith("https://localhost:7080/Account/Login", anonymous.Headers.Location!.ToString());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/Account/Login", new FormUrlEncodedContent([]))).StatusCode);
        var login = await SignIn(client, "https://evil.example/");
        Assert.Equal("/", login.Headers.Location!.ToString());
        var session = Assert.Single(login.Headers.GetValues("Set-Cookie"), x => x.StartsWith("__Host-IsotopeProbe="));
        Assert.Contains("secure", session);
        Assert.Contains("httponly", session);
        Assert.DoesNotContain("mock-access-token", session);
        await using var db = Context();
        var a = await db.Users.SingleAsync();
        Assert.Single(await db.ExternalLogins.ToListAsync());
        var empty = await client.GetStringAsync("/");
        Assert.DoesNotContain("/executions/", empty);
        var b = await new UserService(db).ResolveGoogleAsync("other", null, null);
        var own = Scan(a.Id, "own-marker", "high");
        var other = Scan(b.Id, "other-marker", "critical");
        var historical = Scan(null, "historical-marker", "critical");
        db.AddRange(own, other, historical);
        await db.SaveChangesAsync();
        var html = await client.GetStringAsync("/");
        Assert.Contains("own-marker", html);
        Assert.DoesNotContain("other-marker", html);
        Assert.DoesNotContain("historical-marker", html);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/executions/{own.Id}")).StatusCode);
        foreach (var path in new[] { $"/executions/{other.Id}?severity=critical", $"/findings/{other.Findings[0].Id}",
            $"/executions/{historical.Id}", $"/findings/{historical.Findings[0].Id}", "/executions/999999", "/findings/999999" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        var evidence = await client.GetStringAsync($"/findings/{own.Findings[0].Id}");
        Assert.Contains("&lt;script&gt;", evidence);
        Assert.DoesNotContain("<script>", evidence);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/Account/Logout", new FormUrlEncodedContent([]))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/Account/Logout")).StatusCode);
        var account = await client.GetStringAsync("/Account");
        Assert.Contains(a.Id.ToString(), account);
        var logout = await client.PostAsync("/Account/Logout", Form(account));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Contains(logout.Headers.GetValues("Set-Cookie"), x => x.StartsWith("__Host-IsotopeProbe=;") && x.Contains("expires="));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
        await SignIn(client, "/Account");
        Assert.Equal(2, await db.Users.CountAsync());
        Assert.Equal(2, await db.ExternalLogins.CountAsync());
        // Missing/invalid correlation state must never produce a session.
        using var stranger = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost:7080"), AllowAutoRedirect = false });
        var failure = await stranger.GetAsync("/signin-google?code=fake&state=invalid");
        Assert.Equal("/Account/SignInFailure", failure.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Redirect, (await stranger.GetAsync("/")).StatusCode);
    }

    [PostgresFact]
    public async Task FailedProvisioning_NeverIssuesApplicationSession()
    {
        await using var factory = new AuthWebFactory(connectionString, failProvisioning: true);
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost:7080"), AllowAutoRedirect = false });
        var response = await SignIn(client, "/");
        Assert.Equal("/Account/SignInFailure", response.Headers.Location!.ToString());
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            Assert.DoesNotContain(cookies, x => x.StartsWith("__Host-IsotopeProbe="));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
        await using var db = Context();
        Assert.Empty(await db.Users.ToListAsync());
        Assert.Empty(await db.ExternalLogins.ToListAsync());
    }

    [PostgresFact]
    public async Task InvalidScanOwner_IsRejectedBeforeExecutionIsCreated()
    {
        await using var db = Context();
        var service = new ScanService(new IsotopeProbe.Nuclei.NucleiRunner(new(), "/nonexistent-scanner"), db);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RunAsync("http://localhost", ownerUserId: Guid.NewGuid()));
        Assert.Empty(await db.ScanExecutions.ToListAsync());
        var owner = await new UserService(db).ResolveGoogleAsync("owner", null, null);
        var execution = await service.RunAsync("http://localhost", ownerUserId: owner.Id);
        Assert.Equal(owner.Id, execution.OwnerUserId);
        Assert.Equal(owner.Id, (await db.ScanExecutions.AsNoTracking().SingleAsync()).OwnerUserId);
    }

    private static async Task<HttpResponseMessage> SignIn(HttpClient client, string returnUrl)
    {
        var page = await client.GetStringAsync("/Account/Login?returnUrl=" + Uri.EscapeDataString(returnUrl));
        var challenge = await client.PostAsync("/Account/Login", Form(page, returnUrl));
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var query = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
        Assert.Equal("https://localhost:7080/signin-google", query["redirect_uri"].ToString());
        return await client.GetAsync("/signin-google?code=fake&state=" + Uri.EscapeDataString(query["state"].ToString()));
    }

    private static FormUrlEncodedContent Form(string html, string? returnUrl = null)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success);
        var fields = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", WebUtility.HtmlDecode(match.Groups[1].Value)) };
        if (returnUrl is not null) fields.Add(new("returnUrl", returnUrl));
        return new(fields);
    }

    private static ScanExecution Scan(Guid? owner, string target, params string[] severities) => new()
    {
        OwnerUserId = owner, Target = target, StartedAt = DateTimeOffset.UtcNow,
        Findings = severities.Select(s => new Finding { MatchedAt = "http://localhost", TemplateId = "test", Severity = s, Name = target, RawJson = "{}", Request = "<script>alert(1)</script>" }).ToList()
    };

    private sealed class ProvisioningBarrier : SaveChangesInterceptor
    {
        private int arrivals;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref arrivals) == 2) ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return result;
        }
    }

    private sealed class AuthWebFactory(string connection, bool failProvisioning = false) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ISOTOPEPROBE_CONNECTION_STRING"] = connection,
                ["Authentication:Google:ClientId"] = "test-client",
                ["Authentication:Google:ClientSecret"] = "test-secret"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<GoogleOptions>(GoogleDefaults.AuthenticationScheme,
                    options => options.Backchannel = new HttpClient(new MockGoogle()));
                if (failProvisioning)
                    services.AddDbContext<IsotopeProbeDbContext>(options => options.AddInterceptors(new RejectProvisioning()));
            });
        }
    }

    private sealed class RejectProvisioning : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated user persistence failure.");
    }

    // Only the external network boundary is mocked; production OAuth, cookies and CSRF run normally.
    private sealed class MockGoogle : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.Method == HttpMethod.Post
                ? """{"access_token":"mock-access-token","token_type":"Bearer","expires_in":3600}"""
                : """{"id":"google-a","sub":"google-a","email":"a@example.test","name":"Account A"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
