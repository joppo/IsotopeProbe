using IsotopeProbe.Domain;

namespace IsotopeProbe.Queries;

public sealed record Page<T>(IReadOnlyList<T> Items, int TotalCount, int Skip, int Take);

public sealed record ScanSummary(
    int Id, string Target, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    int? ExitCode, int FindingCount, ScanStatus Status)
{
    public bool Succeeded => Status == ScanStatus.Succeeded;
}

public sealed record SeverityCount(string Severity, int Count);

public sealed record ScanDetails(
    ScanSummary Execution, string StandardError, string? FailureReason, IReadOnlyList<SeverityCount> Severities);

public sealed record FindingSummary(
    int Id, string TemplateId, string Name, string Severity, string MatchedAt);
