using IsotopeProbe.Cli;

namespace IsotopeProbe.Tests;

public sealed class QueryOptionsTests
{
    [Fact]
    public void Parse_UsesDefaultPagination()
    {
        Assert.Equal(new QueryOptions(QueryCommand.ScansList, null, 0, 20),
            QueryOptions.Parse(["scans", "list"]));
        Assert.Equal(new QueryOptions(QueryCommand.FindingsList, 7, 0, 20),
            QueryOptions.Parse(["findings", "list", "--scan", "7"]));
        Assert.Equal(new QueryOptions(QueryCommand.ScansShow, 7),
            QueryOptions.Parse(["scans", "show", "7"]));
    }

    [Fact]
    public void Parse_AcceptsPaginationAndOptionsInAnyOrder()
    {
        Assert.Equal(new QueryOptions(QueryCommand.FindingsList, 7, 10, 100),
            QueryOptions.Parse(["findings", "list", "--take", "100", "--scan", "7", "--skip", "10"]));
    }

    [Theory]
    [InlineData("scans")]
    [InlineData("scans", "unknown")]
    [InlineData("scans", "show")]
    [InlineData("scans", "show", "0")]
    [InlineData("scans", "show", "-1")]
    [InlineData("scans", "show", "abc")]
    [InlineData("scans", "show", "2147483648")]
    [InlineData("scans", "show", "1", "--take", "2")]
    [InlineData("scans", "list", "--skip", "-1")]
    [InlineData("scans", "list", "--take", "0")]
    [InlineData("scans", "list", "--take", "101")]
    [InlineData("scans", "list", "--take", "abc")]
    [InlineData("scans", "list", "--take")]
    [InlineData("scans", "list", "--take", "2", "--take", "3")]
    [InlineData("scans", "list", "--scan", "1")]
    [InlineData("scans", "list", "--unknown", "1")]
    [InlineData("findings", "list")]
    [InlineData("findings", "list", "--scan", "0")]
    [InlineData("findings", "list", "--scan", "1", "--scan", "2")]
    public void Parse_RejectsInvalidQueries(params string[] args)
    {
        Assert.ThrowsAny<ArgumentException>(() => QueryOptions.Parse(args));
    }

    [Fact]
    public void IsQuery_LeavesExistingScanInvocationAlone()
    {
        Assert.False(QueryOptions.IsQuery(["http://localhost:8085", "-templatepath", "~/Templates/sanity/"]));
        Assert.False(QueryOptions.IsQuery([]));
        Assert.True(QueryOptions.IsQuery(["scans", "list"]));
        Assert.True(QueryOptions.IsQuery(["findings", "list"]));
    }
}
