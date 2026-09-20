using System.Diagnostics;
using System.Text.Json;
using IsotopeProbe.Domain;
using IsotopeProbe.Identity;
using IsotopeProbe.Nuclei;
using IsotopeProbe.Persistence;
using IsotopeProbe.Queries;
using IsotopeProbe.Queue;
using IsotopeProbe.Web.Scanning;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace IsotopeProbe.Tests;

public sealed class SubmissionTokenTests
{
    [Fact]
    public void TokensRejectTamperingAndOtherUsers()
    {
        var tokens = new SubmissionTokens(new EphemeralDataProtectionProvider());
        var owner = Guid.NewGuid();
        var value = tokens.Create(owner);
        Assert.NotEqual(Guid.Empty, tokens.Validate(value, owner));
        Assert.Equal(tokens.Validate(value, owner), tokens.Validate(value, owner));
        Assert.Throws<ArgumentException>(() => tokens.Validate(value, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => tokens.Validate(value + "tampered", owner));
        Assert.Throws<ArgumentException>(() => tokens.Validate(null, owner));
    }

    [Fact]
    public void InvalidConfigurationIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new WebScanOptions { TimeoutSeconds = 0 }.Validate());
        Assert.Throws<ArgumentException>(() => new WebScanOptions { QueueLimit = 0 }.Validate());
        Assert.Throws<ArgumentException>(() => new WebScanOptions { ConcurrentScans = 0 }.Validate());
        Assert.Throws<ArgumentException>(() => new WebScanOptions { Targets = [new() { Id = "x", Name = "x", Url = "file:///tmp" }] }.Validate());
    }
}

public sealed class WebQueueTests : IAsyncLifetime
{
    private readonly string schema = "queue_test_" + Guid.NewGuid().ToString("N");
    private readonly string directory = Path.Combine(Path.GetTempPath(), "isotope-queue-" + Guid.NewGuid().ToString("N"));
    private NpgsqlConnection? admin;
    private string connection = "";
    private IsotopeProbeDbContext Context() => new(new DbContextOptionsBuilder<IsotopeProbeDbContext>().UseNpgsql(connection).Options);
    private static WebScanOptions Options() => new()
    {
        Targets = [new() { Id = "local", Name = "Local", Url = "http://localhost:8085" }],
        PollSeconds = 1, TimeoutSeconds = 2
    };

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ISOTOPEPROBE_TEST_CONNECTION_STRING");
        if (string.IsNullOrEmpty(configured)) return;
        admin = new NpgsqlConnection(configured);
        await admin.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin);
        await create.ExecuteNonQueryAsync();
        connection = new NpgsqlConnectionStringBuilder(configured) { SearchPath = schema, Pooling = false }.ConnectionString;
        Directory.CreateDirectory(directory);
        await using var db = Context();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (admin is not null)
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", admin);
            await drop.ExecuteNonQueryAsync();
            await admin.DisposeAsync();
        }
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private async Task<Guid> User()
    {
        await using var db = Context();
        return (await new UserService(db).ResolveGoogleAsync(Guid.NewGuid().ToString(), null, null)).Id;
    }
    private async Task<int> Submit(Guid user, Guid nonce, WebScanOptions? options = null, string target = "local")
    {
        await using var db = Context();
        return await new OwnedScanSubmissionService(db, new(user), options ?? Options()).SubmitAsync(target, nonce);
    }
    private async Task<ScanExecution> Stored(int id)
    {
        await using var db = Context();
        return await db.ScanExecutions.Include(x => x.Findings).SingleAsync(x => x.Id == id);
    }
    private async Task<bool> Dispatch(string executable, CancellationToken token = default)
    {
        await using var db = Context();
        return await new WebScanDispatcher(db, new(new(new(), executable), db)).RunNextAsync(token);
    }
    private async Task<string> Scanner(string tail)
    {
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(path, "#!/usr/bin/python3\nimport sys,time,os,subprocess,json\n" +
            "print('{\"template-id\":\"queue-test\",\"info\":{\"name\":\"Test\",\"severity\":\"info\"},\"matched-at\":\"http://localhost:8085\"}',flush=True)\n" + tail + "\n");
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [PostgresFact]
    public async Task SubmissionSnapshotsConfigurationAndRejectsUnknownTargets_WithoutExecuting()
    {
        var user = await User();
        var options = Options();
        await Assert.ThrowsAsync<ArgumentException>(() => Submit(user, Guid.NewGuid(), options, "http://localhost:8085"));
        await Assert.ThrowsAsync<ArgumentException>(() => Submit(user, Guid.NewGuid(), options, "local --evil"));
        var nonce = Guid.NewGuid();
        var id = await Submit(user, nonce, options);
        options.Targets.Clear();
        options.TemplatePath = "/changed";
        Assert.Equal(id, await Submit(user, nonce, options));
        var saved = await Stored(id);
        Assert.Equal(ScanStatus.Queued, saved.Status);
        Assert.Equal(user, saved.OwnerUserId);
        Assert.Null(saved.StartedAt);
        Assert.NotNull(saved.EnqueuedAt);
        Assert.Equal("http://localhost:8085", saved.Target);
        Assert.EndsWith("Templates/sanity/", saved.TemplatePath);
        Assert.Equal(2, saved.TimeoutSeconds);
        Assert.Empty(saved.Findings);
    }

    [PostgresFact]
    public async Task ConcurrentDuplicateSubmissionsConverge_AndPerUserLimitIncludesRunning()
    {
        var user = await User();
        var nonce = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Submit(user, nonce)));
        Assert.Single(results.Distinct());
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try { await Submit(user, Guid.NewGuid()); return true; }
            catch (ArgumentException) { return false; }
        }));
        Assert.DoesNotContain(true, attempts);
        await using var db = Context();
        await db.ScanExecutions.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ScanStatus.Running));
        await Assert.ThrowsAsync<ArgumentException>(() => Submit(user, Guid.NewGuid()));
        Assert.Equal(1, await db.ScanExecutions.CountAsync());
    }

    [PostgresFact]
    public async Task ConcurrentAdmissionsRespectGlobalAndPerUserLimits()
    {
        var options = Options();
        options.QueueLimit = 3;
        var users = new List<Guid>();
        for (var i = 0; i < 10; i++) users.Add(await User());
        var accepted = await Task.WhenAll(users.Select(async user =>
        {
            try { await Submit(user, Guid.NewGuid(), options); return true; }
            catch (ArgumentException) { return false; }
        }));
        Assert.Equal(3, accepted.Count(x => x));
        await using var db = Context();
        Assert.Equal(3, await db.ScanExecutions.CountAsync());
        await db.ScanExecutions.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ScanStatus.Succeeded));
        var sameUser = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try { await Submit(users[0], Guid.NewGuid(), options); return true; }
            catch (ArgumentException) { return false; }
        }));
        Assert.Equal(1, sameUser.Count(x => x));
    }

    [LifecycleFact]
    public async Task CompetingClaimsExecuteOriginalRecordExactlyOnce()
    {
        var id = await Submit(await User(), Guid.NewGuid());
        var scanner = await Scanner("time.sleep(0.2)");
        var claims = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Dispatch(scanner)));
        Assert.Equal(1, claims.Count(x => x));
        var saved = await Stored(id);
        Assert.Equal(ScanStatus.Succeeded, saved.Status);
        Assert.Single(saved.Findings);
        Assert.Equal(id, saved.Findings[0].ScanExecutionId);
        Assert.True(saved.StartedAt >= saved.EnqueuedAt);
        await using var db = Context();
        Assert.Equal(1, await db.ScanExecutions.CountAsync());
    }

    [LifecycleFact]
    public async Task ProcessFailureAndTimeoutPreserveFindings_AndKillChildren()
    {
        var failed = await Submit(await User(), Guid.NewGuid());
        await Dispatch(await Scanner("sys.exit(7)"));
        Assert.Equal(ScanStatus.Failed, (await Stored(failed)).Status);
        Assert.Equal(7, (await Stored(failed)).ExitCode);
        Assert.Single((await Stored(failed)).Findings);
        var pidPath = Path.Combine(directory, "child");
        var timedOut = await Submit(await User(), Guid.NewGuid());
        await Dispatch(await Scanner("child=subprocess.Popen(['/bin/sleep','60'])\n" +
            $"open({JsonSerializer.Serialize(pidPath)},'w').write(str(child.pid))\ntime.sleep(60)"));
        var saved = await Stored(timedOut);
        Assert.Equal(ScanStatus.Failed, saved.Status);
        Assert.Contains("timed out", saved.FailureReason);
        Assert.Single(saved.Findings);
        var pid = int.Parse(await File.ReadAllTextAsync(pidPath));
        // Linux may retain a killed orphan as a zombie until its parent reaps it.
        var stat = $"/proc/{pid}/stat";
        Assert.True(!File.Exists(stat) || (await File.ReadAllTextAsync(stat)).Split(' ')[2] == "Z");
    }

    [LifecycleFact]
    public async Task WorkerShutdownRetainsFindingsAndQueue_RestartContinuesOldestFirst()
    {
        var options = Options();
        options.TimeoutSeconds = 60;
        var first = await Submit(await User(), Guid.NewGuid(), options);
        var second = await Submit(await User(), Guid.NewGuid(), options);
        var third = await Submit(await User(), Guid.NewGuid(), options);
        var scanner = await Scanner("time.sleep(60)");
        await using var provider = Services(scanner, options);
        using (var worker = new ScanWorker(provider.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<ScanWorker>.Instance))
        {
            await worker.StartAsync(default);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while ((await Stored(first)).Findings.Count == 0) await Task.Delay(25, wait.Token);
            Assert.Equal(ScanStatus.Queued, (await Stored(second)).Status);
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            await worker.StopAsync(shutdown.Token);
        }
        var stopped = await Stored(first);
        Assert.Equal(ScanStatus.Cancelled, stopped.Status);
        Assert.Contains("Application shutdown", stopped.FailureReason);
        Assert.Single(stopped.Findings);
        Assert.Equal(ScanStatus.Queued, (await Stored(second)).Status);
        Assert.Equal(ScanStatus.Queued, (await Stored(third)).Status);
        await using var restartedProvider = Services(await Scanner("pass"), options);
        using var restarted = new ScanWorker(restartedProvider.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<ScanWorker>.Instance);
        await restarted.StartAsync(default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while ((await Stored(third)).Status != ScanStatus.Succeeded) await Task.Delay(25, deadline.Token);
        await restarted.StopAsync(deadline.Token);
        Assert.Equal(ScanStatus.Succeeded, (await Stored(second)).Status);
        Assert.True((await Stored(second)).StartedAt <= (await Stored(third)).StartedAt);
    }

    [LifecycleFact]
    public async Task WorkerContinuesAfterFailedJob()
    {
        var first = await Submit(await User(), Guid.NewGuid());
        var second = await Submit(await User(), Guid.NewGuid());
        var options = Options();
        await using var provider = Services(await Scanner("sys.exit(7)"), options);
        using var worker = new ScanWorker(provider.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<ScanWorker>.Instance);
        await worker.StartAsync(default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while ((await Stored(second)).Status != ScanStatus.Failed) await Task.Delay(25, deadline.Token);
        await worker.StopAsync(deadline.Token);
        Assert.Equal(ScanStatus.Failed, (await Stored(first)).Status);
        Assert.Single((await Stored(first)).Findings);
        Assert.Single((await Stored(second)).Findings);
    }

    private ServiceProvider Services(string executable, WebScanOptions options)
    {
        var services = new ServiceCollection();
        services.AddDbContext<IsotopeProbeDbContext>(o => o.UseNpgsql(connection));
        services.AddScoped(_ => new NucleiRunner(new(), executable));
        services.AddScoped<ScanService>();
        services.AddScoped<WebScanDispatcher>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [PostgresFact]
    public async Task RecoveryOnlyChangesSelectedRunningWebExecution()
    {
        var owner = await User();
        var id = await Submit(owner, Guid.NewGuid());
        await using var db = Context();
        var recovery = new WebScanRecovery(db);
        await Assert.ThrowsAsync<ArgumentException>(() => recovery.MarkInterruptedFailedAsync(id));
        var cli = new ScanExecution { StartedAt = DateTimeOffset.UtcNow };
        db.Add(cli);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => recovery.MarkInterruptedFailedAsync(cli.Id));
        await db.ScanExecutions.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ScanStatus.Running));
        await recovery.MarkInterruptedFailedAsync(id);
        Assert.Equal(ScanStatus.Failed, (await Stored(id)).Status);
        Assert.Equal(ScanStatus.Running, (await Stored(cli.Id)).Status);
        await Assert.ThrowsAsync<ArgumentException>(() => recovery.MarkInterruptedFailedAsync(id));
    }
}
