using IsotopeProbe;
using IsotopeProbe.Nuclei;
using IsotopeProbe.Cli;
using IsotopeProbe.Queries;
using IsotopeProbe.Domain;

ScanOptions? options = null;
QueryOptions? queryOptions = null;
try
{
    if (OperatorConsole.IsOperation(args))
        OperatorConsole.Validate(args);
    else if (QueryOptions.IsQuery(args))
        queryOptions = QueryOptions.Parse(args);
    else
        options = ScanOptions.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine("Usage: IsotopeProbe <target> [-templatepath <path>] [--owner <user-uuid>]");
    Console.Error.WriteLine("       IsotopeProbe scans list [--skip <n>] [--take <n>]");
    Console.Error.WriteLine("       IsotopeProbe scans show <id>");
    Console.Error.WriteLine("       IsotopeProbe findings list --scan <id> [--skip <n>] [--take <n>]");
    Console.Error.WriteLine("       IsotopeProbe scans recover-web <id> --confirmed-stopped");
    Console.Error.WriteLine("       IsotopeProbe scans assign --user <uuid> --executions <id> [<id> ...]");
    Console.Error.WriteLine("       IsotopeProbe groups create <name> [description]");
    Console.Error.WriteLine("       IsotopeProbe groups add|remove --user <uuid> --group <id>");
    Console.Error.WriteLine("       IsotopeProbe groups memberships --user <uuid>");
    return 1;
}

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancelHandler;

try
{
    await using var db = new IsotopeProbeDbContextFactory().CreateDbContext([]);
    if (OperatorConsole.IsOperation(args))
        return await OperatorConsole.RunAsync(args, db, cancellation.Token);
    if (queryOptions is not null)
        return await QueryConsole.RunAsync(queryOptions, new TrustedScanQueryService(db), cancellation.Token);

    var owner = options!.OwnerUserId;
    var configuredOwner = Environment.GetEnvironmentVariable("ISOTOPEPROBE_OWNER_USER_ID");
    if (owner is null && !string.IsNullOrWhiteSpace(configuredOwner))
    {
        if (!Guid.TryParse(configuredOwner, out var id) || id == Guid.Empty)
            throw new ArgumentException("ISOTOPEPROBE_OWNER_USER_ID must be a nonempty internal user UUID.");
        owner = id;
    }
    if (owner is null)
        Console.WriteLine("No owner configured: this scan will be unowned, CLI-only, and invisible to all Web users.");
    var runner = new NucleiRunner(new NucleiFindingParser());
    var scanService = new ScanService(runner, db);
    var execution = await scanService.RunAsync(options!.Target, options.TemplatePath, cancellation.Token, owner);
    Console.WriteLine($"Saved scan execution {execution.Id}.");

    foreach (var finding in execution.Findings)
    {
        Console.WriteLine($"[{finding.Severity}] {finding.Name} ({finding.TemplateId}) at {finding.MatchedAt}");
    }

    if (!execution.Succeeded)
    {
        Console.Error.WriteLine($"Scan {execution.Status} (exit code {execution.ExitCode?.ToString() ?? "unavailable"}).");
        if (execution.FailureReason is not null)
            Console.Error.WriteLine(execution.FailureReason);
        if (!string.IsNullOrWhiteSpace(execution.StandardError))
        {
            Console.Error.WriteLine(execution.StandardError.Trim());
        }

        return execution.Status == ScanStatus.Cancelled ? 130 : 1;
    }

    Console.WriteLine(execution.Findings.Count == 0
        ? "Scan succeeded with zero findings."
        : $"Scan succeeded with {execution.Findings.Count} finding(s).");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Command cancelled.");
    return 130;
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
catch (ScanPersistenceException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Command failed ({exception.GetType().Name}). Check database connectivity and ISOTOPEPROBE_CONNECTION_STRING.");
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}
