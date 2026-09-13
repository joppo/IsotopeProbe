using IsotopeProbe;
using IsotopeProbe.Nuclei;
using IsotopeProbe.Persistence;

ScanOptions options;
try
{
    options = ScanOptions.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine("Usage: IsotopeProbe <target> [-templatepath <path>]");
    return 1;
}

try
{
    await using var db = new IsotopeProbeDbContextFactory().CreateDbContext([]);
    var runner = new NucleiRunner(new NucleiFindingParser());
    var execution = await runner.RunAsync(options.Target, options.TemplatePath);

    db.ScanExecutions.Add(execution);
    await db.SaveChangesAsync();
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
