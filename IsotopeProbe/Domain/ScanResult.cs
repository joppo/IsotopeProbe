namespace IsotopeProbe.Domain;

public sealed record ScanResult(
    IReadOnlyList<Finding> Findings,
    ScanExecutionInfo Execution)
{
    public ScanOutcome Outcome => Execution.Succeeded
        ? Findings.Count == 0
            ? ScanOutcome.SucceededWithNoFindings
            : ScanOutcome.SucceededWithFindings
        : ScanOutcome.Failed;
}
