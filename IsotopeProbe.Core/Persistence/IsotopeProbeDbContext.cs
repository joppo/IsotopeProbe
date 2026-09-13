using IsotopeProbe.Domain;
using Microsoft.EntityFrameworkCore;

namespace IsotopeProbe.Persistence;

public sealed class IsotopeProbeDbContext(
    DbContextOptions<IsotopeProbeDbContext> options)
    : DbContext(options)
{
    public DbSet<ScanExecution> ScanExecutions => Set<ScanExecution>();
    public DbSet<Finding> Findings => Set<Finding>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var execution = modelBuilder.Entity<ScanExecution>();

        execution.ToTable("scan_executions");
        execution.HasKey(x => x.Id);
        execution.Property(x => x.Id).UseIdentityByDefaultColumn();

        execution.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        execution.Property(x => x.FailureReason).HasMaxLength(2000);

        execution.Ignore(x => x.Succeeded);
        execution.Ignore(x => x.Duration);

        execution.HasMany(x => x.Findings)
            .WithOne(x => x.ScanExecution)
            .HasForeignKey(x => x.ScanExecutionId);

        var finding = modelBuilder.Entity<Finding>();

        finding.ToTable("findings");
        finding.HasKey(x => x.Id);
        finding.Property(x => x.Id).UseIdentityByDefaultColumn();
        finding.Property(x => x.Authors).HasColumnType("text[]");
        finding.Property(x => x.Tags).HasColumnType("text[]");
        finding.Property(x => x.RawJson).HasColumnType("jsonb");
    }
}
