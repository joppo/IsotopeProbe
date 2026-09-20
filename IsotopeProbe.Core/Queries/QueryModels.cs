using IsotopeProbe.Domain;

namespace IsotopeProbe.Queries;

public sealed record Page<T>(IReadOnlyList<T> Items, int TotalCount, int Skip, int Take);

public sealed record ScanSummary(
    int Id, string Target, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    int? ExitCode, int FindingCount, ScanStatus Status, DateTimeOffset? EnqueuedAt = null, string? TemplateProfile = null, int? TargetId = null, string? ProfileId = null, string? ProfileVersion = null, string? SnapshotHash = null, int? TemplateCount = null, string? TemplateSourceVersion = null, string? NucleiVersion = null)
{
    public bool Succeeded => Status == ScanStatus.Succeeded;
}

public sealed record SeverityCount(string Severity, int Count);

public sealed record ScanDetails(
    ScanSummary Execution, string StandardError, string? FailureReason, IReadOnlyList<SeverityCount> Severities);

public sealed record FindingSummary(
    int Id, string TemplateId, string Name, string Severity, string MatchedAt);

public sealed record FindingDetails(
    int Id, int ScanExecutionId, string TemplateId, string Name, string Severity,
    string MatchedAt, string? TemplatePath, IReadOnlyList<string> Authors,
    IReadOnlyList<string> Tags, string? Type, string? Host, string? Port, string? Scheme,
    string? Url, string? IpAddress, string? Timestamp, bool? MatcherStatus,
    string? Request, string? Response, string? CurlCommand, string RawJson, string? MatcherName = null);
