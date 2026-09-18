using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using IsotopeProbe.Cli;
using IsotopeProbe.Domain;
using IsotopeProbe.Nuclei;
using IsotopeProbe.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace IsotopeProbe.Tests;

public sealed class LifecycleFactAttribute : FactAttribute
{
    public LifecycleFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Fake scanner lifecycle tests use Linux processes and /usr/bin/python3.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ISOTOPEPROBE_TEST_CONNECTION_STRING")))
            Skip = "Set ISOTOPEPROBE_TEST_CONNECTION_STRING to run isolated PostgreSQL lifecycle tests.";
    }
}

public sealed class LifecycleTests : IAsyncLifetime
{
    private const string FindingJson = """{"template-id":"test","info":{"name":"Test","severity":"info"},"matched-at":"http://localhost/test"}""";
    private readonly string _schema = "lifecycle_test_" + Guid.NewGuid().ToString("N");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "isotope-lifecycle-" + Guid.NewGuid().ToString("N"));
    private NpgsqlConnection? _admin;
    private string _connectionString = "";
    private string PidFile => Path.Combine(_directory, "pids.json");

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ISOTOPEPROBE_TEST_CONNECTION_STRING");
        _admin = new NpgsqlConnection(connectionString);
        await _admin.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE SCHEMA {_schema}", _admin);
        await create.ExecuteNonQueryAsync();
        _connectionString = new NpgsqlConnectionStringBuilder(connectionString)
        { SearchPath = _schema, Pooling = false }.ConnectionString;
        Directory.CreateDirectory(_directory);
        await using var db = Context();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (File.Exists(PidFile))
        {
            foreach (var pid in JsonSerializer.Deserialize<int[]>(await File.ReadAllTextAsync(PidFile))!)
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
        }
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        if (_admin is not null)
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {_schema} CASCADE", _admin);
            await drop.ExecuteNonQueryAsync();
            await _admin.DisposeAsync();
        }
    }

    [LifecycleFact]
    public async Task Success_WithFindingsAndWithZeroFindings_StoresTerminalMetadata()
    {
        foreach (var count in new[] { 1, 0 })
        {
            var path = await Scanner(count == 1 ? Emit() : "pass");
            await using var db = Context();
            var execution = await new ScanService(new NucleiRunner(new(), path), db).RunAsync("http://localhost");
            var saved = await Stored(execution.Id);
            Assert.Equal(ScanStatus.Succeeded, saved.Status);
            Assert.True(saved.Succeeded);
            Assert.Equal(0, saved.ExitCode);
            Assert.NotNull(saved.CompletedAt);
            Assert.Equal(TimeSpan.Zero, saved.StartedAt.Offset);
            Assert.Equal(count, saved.Findings.Count);
        }
    }

    [LifecycleFact]
    public async Task StartupFailure_IsSavedWithoutAnExitCode()
    {
        await using var db = Context();
        var execution = await new ScanService(new NucleiRunner(new(), Path.Combine(_directory, "missing")), db)
            .RunAsync("http://localhost");
        var saved = await Stored(execution.Id);
        Assert.Equal(ScanStatus.Failed, saved.Status);
        Assert.Null(saved.ExitCode);
        Assert.NotNull(saved.CompletedAt);
        Assert.Contains("starting Nuclei", saved.FailureReason);
    }

    [LifecycleFact]
    public async Task NonzeroExit_PreservesFindingsAndDrainsLargeStderrWithoutStoringSecrets()
    {
        var path = await Scanner("sys.stderr.write('Authorization: Bearer secret-token\\n' * 10000)\nsys.stderr.flush()\n" + Emit() + "\nsys.exit(7)");
        await using var db = Context();
        var execution = await new ScanService(new NucleiRunner(new(), path), db).RunAsync("http://localhost");
        var saved = await Stored(execution.Id);
        Assert.Equal(ScanStatus.Failed, saved.Status);
        Assert.Equal(7, saved.ExitCode);
        Assert.Single(saved.Findings);
        Assert.Contains("code 7", saved.FailureReason);
        Assert.Contains("stderr characters", saved.StandardError);
        Assert.DoesNotContain("secret-token", saved.StandardError);
        Assert.True(saved.StandardError.Length < 2000);
    }

    [LifecycleFact]
    public async Task StderrAlone_DoesNotFailSuccessfulScan()
    {
        var path = await Scanner("sys.stderr.write('password=secret\\n')");
        await using var db = Context();
        var execution = await new ScanService(new NucleiRunner(new(), path), db).RunAsync("http://localhost");
        Assert.Equal(ScanStatus.Succeeded, execution.Status);
        Assert.DoesNotContain("secret", execution.StandardError);
    }

    [LifecycleFact]
    public async Task MalformedOutput_FailsAndRetainsEarlierFindings()
    {
        var path = await Scanner(Emit() + "\nprint('invalid secret-token', flush=True)\ntime.sleep(60)");
        await using var db = Context();
        var execution = await new ScanService(new NucleiRunner(new(), path), db).RunAsync("http://localhost");
        var saved = await Stored(execution.Id);
        Assert.Equal(ScanStatus.Failed, saved.Status);
        Assert.Single(saved.Findings);
        Assert.Contains("parsing", saved.FailureReason);
        Assert.DoesNotContain("secret-token", saved.FailureReason);
        Assert.NotNull(saved.ExitCode);
    }

    [LifecycleFact]
    public async Task FindingWriteFailure_DoesNotRollbackEarlierFindingsOrBlockFinalization()
    {
        // PostgreSQL jsonb rejects a JSON null character, while the JSON parser accepts it.
        var invalidForPostgres = FindingJson.Replace("Test", "Test\\u0000");
        var path = await Scanner(Emit() + "\n" + Emit(invalidForPostgres));
        await using var db = Context();
        var execution = await new ScanService(new NucleiRunner(new(), path), db).RunAsync("http://localhost");
        var saved = await Stored(execution.Id);
        Assert.Equal(ScanStatus.Failed, saved.Status);
        Assert.Single(saved.Findings);
        Assert.Contains("saving a finding", saved.FailureReason);
    }

    [LifecycleFact]
    public async Task Cancellation_StopsProcessTreeAndRetainsIncrementallySavedFindings()
    {
        var path = await WaitingScanner();
        await using var db = Context();
        using var cancellation = new CancellationTokenSource();
        var running = new ScanService(new NucleiRunner(new(), path), db).RunAsync("http://localhost", cancellationToken: cancellation.Token);
        var id = await WaitForFinding();
        var before = await Stored(id);
        Assert.Equal(ScanStatus.Running, before.Status);
        Assert.False(before.Succeeded);
        Assert.Null(before.CompletedAt);
        Assert.Null(before.ExitCode);
        cancellation.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(25));
        Assert.Equal(ScanStatus.Cancelled, result.Status);
        var saved = await Stored(id);
        Assert.Equal(ScanStatus.Cancelled, saved.Status);
        Assert.NotNull(saved.CompletedAt);
        Assert.Single(saved.Findings);
        foreach (var pid in JsonSerializer.Deserialize<int[]>(await File.ReadAllTextAsync(PidFile))!)
            Assert.True(Stopped(pid), $"Scanner process {pid} is still running.");
    }

    [LifecycleFact]
    public async Task CancellationDuringFinalWrite_DoesNotOverwriteSuccessfulCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        await using var db = Context(new FinalWriteInterceptor(() => cancellation.Cancel()));
        var path = await Scanner(Emit());
        var result = await new ScanService(new NucleiRunner(new(), path), db)
            .RunAsync("http://localhost", cancellationToken: cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(ScanStatus.Succeeded, (await Stored(result.Id)).Status);
    }

    [LifecycleFact]
    public async Task FinalWriteFailure_ReportsUnconfirmedStateInsteadOfClaimingSuccess()
    {
        await using var db = Context(new FinalWriteInterceptor(() => throw new IOException("password=secret")));
        var path = await Scanner(Emit());
        var error = await Assert.ThrowsAsync<ScanPersistenceException>(() =>
            new ScanService(new NucleiRunner(new(), path), db).RunAsync("http://localhost"));
        Assert.Contains("Could not confirm final status", error.Message);
        Assert.DoesNotContain("secret", error.Message);
        await using var observer = Context();
        var saved = await observer.ScanExecutions.Include(x => x.Findings).SingleAsync();
        Assert.Equal(ScanStatus.Running, saved.Status);
        Assert.False(saved.Succeeded);
        Assert.Single(saved.Findings);
    }

    [LifecycleFact]
    public async Task CtrlC_FromCli_PersistsCancelledAndReturns130()
    {
        await WaitingScanner();
        using var cli = StartCli();
        await WaitForFinding(cli);
        using var signal = Process.Start("/bin/kill", $"-INT {cli.Id}")!;
        await signal.WaitForExitAsync();
        await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
        Assert.Equal(130, cli.ExitCode);
        await using var observer = Context();
        var saved = await observer.ScanExecutions.Include(x => x.Findings).SingleAsync();
        Assert.Equal(ScanStatus.Cancelled, saved.Status);
        Assert.Single(saved.Findings);
    }

    [LifecycleFact]
    public async Task ForcedCliTermination_LeavesRunningRatherThanSuccess()
    {
        await WaitingScanner();
        using var cli = StartCli();
        var id = await WaitForFinding(cli);
        cli.Kill(); // Deliberately bypass Ctrl+C cleanup, as a crash would.
        await cli.WaitForExitAsync();
        var saved = await Stored(id);
        Assert.Equal(ScanStatus.Running, saved.Status);
        Assert.False(saved.Succeeded);
        Assert.Null(saved.CompletedAt);
        Assert.Single(saved.Findings);
        using var query = StartCli("scans", "show", id.ToString());
        var output = await query.StandardOutput.ReadToEndAsync();
        await query.WaitForExitAsync();
        Assert.Equal(0, query.ExitCode);
        Assert.Contains("does not prove the process is still alive", output);
        Assert.Contains("not completed", output);
        Assert.Contains("Status: Running", output);
    }

    [LifecycleFact]
    public async Task Migration_BackfillsOnlyStatusAndPreservesHistoricalMetadata()
    {
        await using var db = Context();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260911123031_InitialCreate");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO scan_executions ("Target", "StartedAt", "CompletedAt", "ExitCode", "StandardError")
            VALUES ('http://localhost/old-success', '2026-01-01T00:00:00Z', '2026-01-01T00:01:00Z', 0, 'old diagnostic'),
                   ('http://localhost/old-failure', '2026-01-02T00:00:00Z', '2026-01-02T00:01:00Z', 7, 'old diagnostic');
            """);
        await migrator.MigrateAsync();
        var scans = await db.ScanExecutions.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(new[] { ScanStatus.Succeeded, ScanStatus.Failed }, scans.Select(x => x.Status));
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), scans[0].StartedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:01:00Z"), scans[0].CompletedAt);
        Assert.Equal(7, scans[1].ExitCode);
        Assert.All(scans, x => Assert.Equal("old diagnostic", x.StandardError));
        Assert.All(scans, x => Assert.Null(x.FailureReason));
        Assert.Contains("20260911123031_InitialCreate", await db.Database.GetAppliedMigrationsAsync());
    }

    [LifecycleFact]
    public async Task Finalization_DoesNotOverwriteAnExistingTerminalState()
    {
        var path = await Scanner(Emit());
        await using var db = Context(new FinalWriteInterceptor(() =>
        {
            using var other = Context();
            other.ScanExecutions.ExecuteUpdate(setters => setters.SetProperty(x => x.Status, ScanStatus.Cancelled));
        }));
        var error = await Assert.ThrowsAsync<ScanPersistenceException>(() =>
            new ScanService(new NucleiRunner(new(), path), db).RunAsync("http://localhost"));
        Assert.Contains("No terminal state was overwritten", error.Message);
        await using var observer = Context();
        Assert.Equal(ScanStatus.Cancelled, (await observer.ScanExecutions.SingleAsync()).Status);
    }

    [LifecycleFact]
    public async Task InitialWriteFailure_DoesNotLaunchScannerOrClaimPersistence()
    {
        var marker = Path.Combine(_directory, "launched");
        var path = await Scanner($"open({JsonSerializer.Serialize(marker)}, 'w').close()");
        await using var db = Context(new RejectSaveInterceptor());
        var error = await Assert.ThrowsAsync<ScanPersistenceException>(() =>
            new ScanService(new NucleiRunner(new(), path), db).RunAsync("http://localhost"));
        Assert.Contains("Nuclei was not launched", error.Message);
        Assert.False(File.Exists(marker));
        await using var observer = Context();
        Assert.Empty(await observer.ScanExecutions.ToListAsync());
    }

    [LifecycleFact]
    public async Task CancellationDuringFindingWrite_FinishesThatWriteBeforeFinalizing()
    {
        using var cancellation = new CancellationTokenSource();
        await using var db = Context(new CancelFindingWriteInterceptor(cancellation));
        var path = await Scanner(Emit() + "\ntime.sleep(60)");
        var result = await new ScanService(new NucleiRunner(new(), path), db)
            .RunAsync("http://localhost", cancellationToken: cancellation.Token);
        var saved = await Stored(result.Id);
        Assert.Equal(ScanStatus.Cancelled, saved.Status);
        Assert.Single(saved.Findings);
    }

    private IsotopeProbeDbContext Context(params IInterceptor[] interceptors) => new(
        new DbContextOptionsBuilder<IsotopeProbeDbContext>().UseNpgsql(_connectionString)
            .AddInterceptors(interceptors).Options);

    private async Task<ScanExecution> Stored(int id)
    {
        await using var observer = Context();
        return await observer.ScanExecutions.AsNoTracking().Include(x => x.Findings).SingleAsync(x => x.Id == id);
    }

    private async Task<int> WaitForFinding(Process? cli = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            if (cli?.HasExited == true)
                throw new InvalidOperationException("CLI exited before producing a finding: " + await cli.StandardError.ReadToEndAsync());
            await using var observer = Context();
            var id = await observer.Findings.Select(x => (int?)x.ScanExecutionId).FirstOrDefaultAsync(timeout.Token);
            if (id is not null) return id.Value;
            await Task.Delay(25, timeout.Token);
        }
    }

    private async Task<string> WaitingScanner() => await Scanner(
        "child = subprocess.Popen(['/bin/sleep', '60'])\n" +
        $"with open({JsonSerializer.Serialize(PidFile)}, 'w') as f: json.dump([os.getpid(), child.pid], f)\n" +
        Emit() + "\ntime.sleep(60)");

    private async Task<string> Scanner(string body)
    {
        var path = Path.Combine(_directory, "nuclei");
        await File.WriteAllTextAsync(path, "#!/usr/bin/python3\nimport sys, time, os, json, subprocess\n" + body + "\n");
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private Process StartCli(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        // The Web test host moves shared Microsoft.Extensions dependencies into the
        // ASP.NET shared framework. Execute the copied CLI with this test host's
        // dependency/runtime manifests so those assemblies remain resolvable.
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.ChangeExtension(typeof(LifecycleTests).Assembly.Location, ".runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.ChangeExtension(typeof(LifecycleTests).Assembly.Location, ".deps.json"));
        start.ArgumentList.Add(typeof(QueryOptions).Assembly.Location);
        foreach (var argument in arguments.Length == 0 ? ["http://localhost"] : arguments)
            start.ArgumentList.Add(argument);
        start.Environment.Remove("ISOTOPEPROBE_OWNER_USER_ID");
        start.Environment["ISOTOPEPROBE_CONNECTION_STRING"] = _connectionString;
        start.Environment["PATH"] = _directory + ":" + Environment.GetEnvironmentVariable("PATH");
        return Process.Start(start)!;
    }

    private static string Emit(string json = FindingJson) => $"print({JsonSerializer.Serialize(json)}, flush=True)";

    private static bool Stopped(int pid)
    {
        var status = $"/proc/{pid}/status";
        if (!File.Exists(status)) return true;
        try { return File.ReadLines(status).Any(x => x.StartsWith("State:") && x.Contains('Z')); }
        catch (FileNotFoundException) { return true; }
    }

    private sealed class CancelFindingWriteInterceptor(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Finding>().Any(x => x.State == EntityState.Added))
                cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RejectSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new IOException("password=secret");
    }

    private sealed class FinalWriteInterceptor(Action action) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE scan_executions")) action();
            return ValueTask.FromResult(result);
        }
    }
}
