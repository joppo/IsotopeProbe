using IsotopeProbe.Domain;

namespace IsotopeProbe.Tests;

public sealed class ScanExecutionTests
{
    [Fact]
    public void NewExecution_IsRunningWithoutAnInventedExitCodeOrCompletionTime()
    {
        var execution = new ScanExecution();
        Assert.Equal(ScanStatus.Running, execution.Status);
        Assert.False(execution.Succeeded);
        Assert.Null(execution.ExitCode);
        Assert.Null(execution.CompletedAt);
        Assert.Null(execution.Duration);
        execution.ExitCode = 0;
        Assert.False(execution.Succeeded);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 1, true)]
    [InlineData(2, 0, false)]
    [InlineData(2, 1, false)]
    public void Succeeded_DependsOnStatusAndRetainsFindings(
        int exitCode, int findingCount, bool expectedSucceeded)
    {
        List<Finding> findings = findingCount == 0 ? [] : [new Finding
        {
            RawJson = "{}",
            TemplateId = "template",
            Name = "name",
            Severity = "info",
            MatchedAt = "https://example.com"
        }];
        var startedAt = DateTimeOffset.Parse("2026-08-31T12:00:00Z");
        var execution = new ScanExecution
        {
            Target = "https://example.com",
            StartedAt = startedAt,
            CompletedAt = startedAt.AddSeconds(2),
            ExitCode = exitCode,
            Status = expectedSucceeded ? ScanStatus.Succeeded : ScanStatus.Failed,
            StandardError = exitCode == 0 ? string.Empty : "nuclei error",
            Findings = findings
        };

        Assert.Equal(expectedSucceeded, execution.Succeeded);
        Assert.Equal(exitCode, execution.ExitCode);
        Assert.Equal(findingCount, execution.Findings.Count);
        Assert.Same(findings, execution.Findings);
        Assert.Equal(TimeSpan.FromSeconds(2), execution.Duration);
    }
}
