using IsotopeProbe.Domain;
using Microsoft.EntityFrameworkCore;

namespace IsotopeProbe.Persistence;

public sealed class IsotopeProbeDbContext(
    DbContextOptions<IsotopeProbeDbContext> options)
    : DbContext(options)
{
    public DbSet<ScanExecution> ScanExecutions => Set<ScanExecution>();
    public DbSet<Target> Targets => Set<Target>();
    public DbSet<Finding> Findings => Set<Finding>();

    public DbSet<User> Users => Set<User>();
    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<UserGroup> UserGroups => Set<UserGroup>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var user = modelBuilder.Entity<User>();
        user.ToTable("users");
        user.HasKey(x => x.Id);
        var login = modelBuilder.Entity<ExternalLogin>();
        login.ToTable("external_logins");
        login.HasKey(x => new { x.Provider, x.Subject });
        login.Property(x => x.Provider).HasMaxLength(100);
        login.Property(x => x.Subject).HasMaxLength(255);
        login.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        var group = modelBuilder.Entity<Group>();
        group.ToTable("groups");
        group.HasKey(x => x.Id);
        group.Property(x => x.Name).HasMaxLength(100);
        group.Property(x => x.NormalizedName).HasMaxLength(100);
        group.HasIndex(x => x.NormalizedName).IsUnique();
        var membership = modelBuilder.Entity<UserGroup>();
        membership.ToTable("user_groups");
        membership.HasKey(x => new { x.UserId, x.GroupId });
        membership.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        membership.HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId);
        var target = modelBuilder.Entity<Target>();
        target.ToTable("targets");
        target.HasKey(x => x.Id);
        target.Property(x => x.Id).UseIdentityByDefaultColumn();
        target.Property(x => x.Url).UseCollation("C");
        target.HasIndex(x => new { x.OwnerUserId, x.Url }).IsUnique();
        target.HasAlternateKey(x => new { x.Id, x.OwnerUserId });
        target.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        var execution = modelBuilder.Entity<ScanExecution>();

        execution.ToTable("scan_executions", t => t.HasCheckConstraint(
            "CK_scan_executions_TargetRequiresOwner", "\"TargetId\" IS NULL OR \"OwnerUserId\" IS NOT NULL"));
        execution.HasOne<Target>().WithMany().HasForeignKey(x => new { x.TargetId, x.OwnerUserId })
            .HasPrincipalKey(x => new { x.Id, x.OwnerUserId }).OnDelete(DeleteBehavior.Restrict);
        execution.HasKey(x => x.Id);
        execution.Property(x => x.Id).UseIdentityByDefaultColumn();

        execution.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        execution.Property(x => x.Source).HasConversion<string>().HasMaxLength(20).HasDefaultValue(ScanSource.Cli);
        execution.HasIndex(x => new { x.OwnerUserId, x.SubmissionId }).IsUnique();
        execution.HasIndex(x => new { x.Source, x.Status, x.EnqueuedAt, x.Id });
        execution.Property(x => x.FailureReason).HasMaxLength(2000);

        execution.HasOne(x => x.OwnerUser).WithMany().HasForeignKey(x => x.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);
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
