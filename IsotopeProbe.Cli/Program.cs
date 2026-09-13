using IsotopeProbe;
using IsotopeProbe.Nuclei;
using IsotopeProbe.Cli;
using IsotopeProbe.Queries;

ScanOptions? options = null;
QueryOptions? queryOptions = null;
try
{
    if (QueryOptions.IsQuery(args))
        queryOptions = QueryOptions.Parse(args);
    else
        options = ScanOptions.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine("Usage: IsotopeProbe <target> [-templatepath <path>]");
    Console.Error.WriteLine("       IsotopeProbe scans list [--skip <n>] [--take <n>]");
    Console.Error.WriteLine("       IsotopeProbe scans show <id>");
    Console.Error.WriteLine("       IsotopeProbe findings list --scan <id> [--skip <n>] [--take <n>]");
    return 1;
}

try
{
    await using var db = new IsotopeProbeDbContextFactory().CreateDbContext([]);
    if (queryOptions is not null)
        return await QueryConsole.RunAsync(queryOptions, new ScanQueryService(db));

    var runner = new NucleiRunner(new NucleiFindingParser());
    var scanService = new ScanService(runner, db);
    var execution = await scanService.RunAsync(options!.Target, options.TemplatePath);
    Console.WriteLine($"Saved scan execution {execution.Id}.");

    foreach (var finding in execution.Findings)
    {
        Console.WriteLine($"[{finding.Severity}] {finding.Name} ({finding.TemplateId}) at {finding.MatchedAt}");
    }

    if (!execution.Succeeded)
    {
        Console.Error.WriteLine($"Nuclei failed with exit code {execution.ExitCode}.");
        if (!string.IsNullOrWhiteSpace(execution.StandardError))
        {
            Console.Error.WriteLine(execution.StandardError.Trim());
        }

        return 1;
    }

    Console.WriteLine(execution.Findings.Count == 0
        ? "Scan succeeded with zero findings."
        : $"Scan succeeded with {execution.Findings.Count} finding(s).");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
