using IsotopeProbe.Nuclei;

if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("Usage: IsotopeProbe <target>");
    return 1;
}

try
{
    var runner = new NucleiRunner(new NucleiFindingParser());
    var result = await runner.RunAsync(args[0]);

    foreach (var finding in result.Findings)
    {
        Console.WriteLine($"[{finding.Severity}] {finding.Name} ({finding.TemplateId}) at {finding.MatchedAt}");
    }

    switch (result.Outcome)
    {
        case IsotopeProbe.Domain.ScanOutcome.SucceededWithNoFindings:
            Console.WriteLine("Scan succeeded with zero findings.");
            return 0;

        case IsotopeProbe.Domain.ScanOutcome.SucceededWithFindings:
            Console.WriteLine($"Scan succeeded with {result.Findings.Count} finding(s).");
            return 0;

        case IsotopeProbe.Domain.ScanOutcome.Failed:
            Console.Error.WriteLine($"Nuclei failed with exit code {result.Execution.ExitCode}.");
            if (!string.IsNullOrWhiteSpace(result.Execution.StandardError))
            {
                Console.Error.WriteLine(result.Execution.StandardError.Trim());
            }

            return 1;

        default:
            throw new InvalidOperationException($"Unknown scan outcome: {result.Outcome}.");
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
