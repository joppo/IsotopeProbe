namespace IsotopeProbe.Domain;

public sealed class ScanExecution
{
    public int Id { get; set; }
    public int? TargetId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public User? OwnerUser { get; set; }

    public ScanSource Source { get; set; } = ScanSource.Cli;
    public DateTimeOffset? EnqueuedAt { get; set; }
    public Guid? SubmissionId { get; set; }
    public string? TemplatePath { get; set; }
    public string? TemplateProfile { get; set; }
    public string? ProfileId { get; set; }
    public string? ProfileVersion { get; set; }
    public string? SnapshotHash { get; set; }
    public int? TemplateCount { get; set; }
    public string? TemplateSourceVersion { get; set; }
    public string? NucleiVersion { get; set; }
    public int? TimeoutSeconds { get; set; }

    public string Target { get; set; } = "";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? ExitCode { get; set; }
    public ScanStatus Status { get; set; } = ScanStatus.Running;
    public string? FailureReason { get; set; }
    public string StandardError { get; set; } = "";

    public List<Finding> Findings { get; set; } = [];

    public bool Succeeded => Status == ScanStatus.Succeeded;
    public TimeSpan? Duration => CompletedAt - StartedAt;
}
