using IsotopeProbe.Persistence;
using IsotopeProbe.Queries;
using Microsoft.EntityFrameworkCore;

namespace IsotopeProbe.Tests;

public sealed class QueryValidationTests
{
    [Theory]
    [InlineData(-1, 20)]
    [InlineData(0, 0)]
    [InlineData(0, 101)]
    public async Task Services_RejectInvalidPaginationBeforeDatabaseAccess(int skip, int take)
    {
        using var db = CreateContext();
        var queries = new TrustedScanQueryService(db);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => queries.ListScansAsync(skip, take));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => queries.ListFindingsAsync(1, skip, take));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Services_RejectInvalidIdsBeforeDatabaseAccess(int id)
    {
        using var db = CreateContext();
        var queries = new TrustedScanQueryService(db);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => queries.GetScanAsync(id));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => queries.GetFindingAsync(id));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => queries.ListFindingsAsync(id));
    }

    private static IsotopeProbeDbContext CreateContext() => new(
        new DbContextOptionsBuilder<IsotopeProbeDbContext>()
            .UseNpgsql("Host=localhost;Database=unused_validation_test").Options);
}
