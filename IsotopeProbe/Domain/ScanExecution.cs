namespace IsotopeProbe.Domain;

public sealed class ScanExecution
{
    public int Id { get; set; }

    public string Target { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public int ExitCode { get; set; }
    public string StandardError { get; set; } = "";

    public List<Finding> Findings { get; set; } = [];

    public bool Succeeded => ExitCode == 0;
    public TimeSpan Duration => CompletedAt - StartedAt;
}
