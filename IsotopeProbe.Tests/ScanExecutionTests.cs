using IsotopeProbe.Domain;

namespace IsotopeProbe.Tests;

public sealed class ScanExecutionTests
{
    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 1, true)]
    [InlineData(2, 0, false)]
    [InlineData(2, 1, false)]
    public void Succeeded_DependsOnExitCodeAndRetainsFindings(
        int exitCode, int findingCount, bool expectedSucceeded)
    {
        IReadOnlyList<Finding> findings = findingCount == 0 ? [] : [new Finding
        {
            TemplateId = "template",
            Name = "name",
            Severity = "info",
            MatchedAt = "https://example.com"
        }];
        var startedAt = DateTimeOffset.Parse("2026-08-31T12:00:00Z");
        var execution = new ScanExecution(
            "https://example.com",
            startedAt,
            startedAt.AddSeconds(2),
            exitCode,
            exitCode == 0 ? string.Empty : "nuclei error",
            findings);

        Assert.Equal(expectedSucceeded, execution.Succeeded);
        Assert.Equal(exitCode, execution.ExitCode);
        Assert.Equal(findingCount, execution.Findings.Count);
        Assert.Same(findings, execution.Findings);
        Assert.Equal(TimeSpan.FromSeconds(2), execution.Duration);
    }
}
