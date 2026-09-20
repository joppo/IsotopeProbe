using System.Text.Json;
using IsotopeProbe.Comparisons;
using IsotopeProbe.Nuclei;

namespace IsotopeProbe.Tests;

public sealed class MatcherNameTests
{
    [Theory]
    [InlineData("\"Case Sensitive \"", "Case Sensitive ")]
    [InlineData("null", null)]
    [InlineData("\"\"", null)]
    [InlineData("\" \\t\\n\"", null)]
    [InlineData("\"\\u00a0\\u2003\"", null)]
    [InlineData("true", null)]
    [InlineData("123", null)]
    [InlineData("[]", null)]
    [InlineData("{\"nested\":\"name\"}", null)]
    public void OptionalFieldIsTolerantWithoutChangingRawEvidenceOrMatcherStatus(string value, string? expected)
    {
        var json = "{\"template-id\":\"test\",\"info\":{\"name\":\"Test\",\"severity\":\"info\"},\"matched-at\":\"http://localhost\",\"matcher-status\":false,\"matcher-name\":" + value + "}";
        var finding = new NucleiFindingParser().Parse(json);
        Assert.Equal(expected, finding.MatcherName);
        Assert.False(finding.MatcherStatus);
        Assert.Equal(json, finding.RawJson);
    }
    [Fact]
    public void MissingNameStaysNullAndUnrelatedErrorsStillFail()
    {
        const string json = """{"template-id":"test","info":{"name":"Test","severity":"info"},"matched-at":"http://localhost","matcher-status":true} """;
        Assert.Null(new NucleiFindingParser().Parse(json).MatcherName);
        Assert.True(new NucleiFindingParser().Parse(json).MatcherStatus);
        Assert.Throws<JsonException>(() => new NucleiFindingParser().Parse(json.Replace("true", "\"bad\"")));
        Assert.Throws<JsonException>(() => new NucleiFindingParser().Parse(json.Replace("\"test\"", "{}")));
    }
}

public sealed class FindingComparisonTests
{
    private static ComparisonFinding F(int id, string template = "t", string location = "http://localhost/Path?q=A", string? matcher = null, string severity = "high") => new(id, template, location, matcher, severity);
    [Fact]
    public void SetsRetainOccurrencesAndSeverityDoesNotChangeIdentity()
    {
        var result = FindingComparison.Compare([F(1), F(2), F(3, "removed")], [F(4, severity: "low"), F(5, "new")]);
        Assert.Equal(new ComparisonCounts(1, 1, 1, 0, 0, 3, 2, 0), result.Counts);
        var both = Assert.Single(result.Categories[ComparisonCategory.Both]);
        Assert.Equal(new[] { 1, 2 }, both.Older.Select(x => x.Id));
        Assert.Equal("low", Assert.Single(both.Newer).Severity);
    }
    [Fact]
    public void OptionalNamesAreExactAndMissingIsNeverAWildcard()
    {
        var absent = FindingComparison.Compare([F(1, matcher: " \t")], [F(2, matcher: "")]);
        Assert.Equal(1, absent.Counts.Both);
        var changed = FindingComparison.Compare([F(1, matcher: "A")], [F(2, matcher: "a")]);
        Assert.Equal(0, changed.Counts.Both); Assert.Equal(1, changed.Counts.New); Assert.Equal(1, changed.Counts.NoLonger);
        var missing = FindingComparison.Compare([F(1)], [F(2, matcher: "A")]);
        Assert.Equal(1, missing.Counts.MatcherAvailabilityWarnings); Assert.Equal(0, missing.Counts.Both);
        Assert.Equal(1, FindingComparison.Compare([F(1, matcher: "A")], [F(2)]).Counts.MatcherAvailabilityWarnings);
        var mixed = FindingComparison.Compare([F(1), F(2, matcher: "A")], [F(3), F(4, matcher: "A")]);
        Assert.Equal(2, mixed.Counts.Both); Assert.Equal(0, mixed.Counts.MatcherAvailabilityWarnings);
    }
    [Theory]
    [InlineData("http://localhost/path?q=A")]
    [InlineData("http://localhost/Path?q=a")]
    [InlineData("http://localhost/Path")]
    [InlineData("https://localhost/Path?q=A")]
    public void LocationsAreNotNormalized(string location) => Assert.Equal(0, FindingComparison.Compare([F(1)], [F(2, location: location)]).Counts.Both);
    [Fact]
    public void StructuredKeysAvoidDelimiterCollisionsAndExcludeIncompleteRecords()
    {
        var result = FindingComparison.Compare([F(1, "a|b", "c"), F(2, " "), F(3, location: "")], [F(4, "a", "b|c"), F(5, location: "\t")]);
        Assert.Equal(0, result.Counts.Both);
        Assert.Equal(2, result.Counts.IncompleteOlder); Assert.Equal(1, result.Counts.IncompleteNewer);
        Assert.Equal(1, result.Counts.New); Assert.Equal(1, result.Counts.NoLonger);
        Assert.Equal(new[] { 2, 3 }, result.Categories[ComparisonCategory.IncompleteOlder].SelectMany(x => x.Older).Select(x => x.Id));
        Assert.NotEqual(FindingComparison.Key(F(1, "T")), FindingComparison.Key(F(2, "t")));
    }
    [Fact]
    public void CancellationIsHonored()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => FindingComparison.Compare([F(1)], [F(2)], cancelled.Token));
    }
}
