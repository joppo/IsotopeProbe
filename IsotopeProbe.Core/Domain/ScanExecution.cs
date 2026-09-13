namespace IsotopeProbe.Domain;

public sealed class ScanExecution
{
    public int Id { get; set; }

    public string Target { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? ExitCode { get; set; }
    public ScanStatus Status { get; set; } = ScanStatus.Running;
    public string? FailureReason { get; set; }
    public string StandardError { get; set; } = "";

    public List<Finding> Findings { get; set; } = [];

    public bool Succeeded => Status == ScanStatus.Succeeded;
    public TimeSpan? Duration => CompletedAt - StartedAt;
}
