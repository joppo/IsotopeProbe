using IsotopeProbe.Nuclei;

if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("Usage: IsotopeProbe <target>");
    return 1;
}

try
{
    var runner = new NucleiRunner(new NucleiFindingParser());
    var execution = await runner.RunAsync(args[0]);

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
