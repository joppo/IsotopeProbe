namespace IsotopeProbe.Domain;

public sealed record ScanExecutionInfo(
    string Target,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int ExitCode,
    string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    public TimeSpan Duration => CompletedAt - StartedAt;
}
