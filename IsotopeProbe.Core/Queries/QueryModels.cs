namespace IsotopeProbe.Queries;

public sealed record Page<T>(IReadOnlyList<T> Items, int TotalCount, int Skip, int Take);

public sealed record ScanSummary(
    int Id, string Target, DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    int ExitCode, int FindingCount)
{
    public bool Succeeded => ExitCode == 0;
}

public sealed record SeverityCount(string Severity, int Count);

public sealed record ScanDetails(
    ScanSummary Execution, string StandardError, IReadOnlyList<SeverityCount> Severities);

public sealed record FindingSummary(
    int Id, string TemplateId, string Name, string Severity, string MatchedAt);
