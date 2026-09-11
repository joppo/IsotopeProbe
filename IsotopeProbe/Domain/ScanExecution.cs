namespace IsotopeProbe.Domain;

public sealed record ScanExecution(
    string Target,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int ExitCode,
    string StandardError,
    IReadOnlyList<Finding> Findings)
{
    public bool Succeeded => ExitCode == 0;

    public TimeSpan Duration => CompletedAt - StartedAt;
}
