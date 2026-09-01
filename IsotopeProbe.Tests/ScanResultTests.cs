using IsotopeProbe.Domain;

namespace IsotopeProbe.Tests;

public sealed class ScanResultTests
{
    [Fact]
    public void Outcome_WhenExitCodeIsZeroAndFindingsExist_IsSucceededWithFindings()
    {
        var result = CreateResult(
            exitCode: 0,
            [CreateFinding()]);

        Assert.Equal(ScanOutcome.SucceededWithFindings, result.Outcome);
    }

    [Fact]
    public void Outcome_WhenExitCodeIsZeroAndFindingsAreEmpty_IsSucceededWithNoFindings()
    {
        var result = CreateResult(exitCode: 0, []);

        Assert.Equal(ScanOutcome.SucceededWithNoFindings, result.Outcome);
    }

    [Fact]
    public void Outcome_WhenExitCodeIsNonzero_IsFailedEvenWhenFindingsExist()
    {
        var result = CreateResult(
            exitCode: 2,
            [CreateFinding()]);

        Assert.Equal(ScanOutcome.Failed, result.Outcome);
        Assert.Equal(2, result.Execution.ExitCode);
    }

    private static ScanResult CreateResult(int exitCode, IReadOnlyList<Finding> findings)
    {
        var startedAt = DateTimeOffset.Parse("2026-08-31T12:00:00Z");
        var execution = new ScanExecutionInfo(
            "https://example.com",
            startedAt,
            startedAt.AddSeconds(2),
            exitCode,
            exitCode == 0 ? string.Empty : "nuclei error");

        return new ScanResult(findings, execution);
    }

    private static Finding CreateFinding() => new()
    {
        TemplateId = "template",
        Name = "name",
        Severity = "info",
        MatchedAt = "https://example.com"
    };
}
