using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IsotopeProbe.Persistence;

public sealed class IsotopeProbeDbContextFactory : IDesignTimeDbContextFactory<IsotopeProbeDbContext>
{
    public IsotopeProbeDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ISOTOPEPROBE_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Set ISOTOPEPROBE_CONNECTION_STRING to your PostgreSQL connection string.");
        }

        var options = new DbContextOptionsBuilder<IsotopeProbeDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new IsotopeProbeDbContext(options);
    }
}
