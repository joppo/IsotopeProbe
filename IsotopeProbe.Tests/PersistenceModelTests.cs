using IsotopeProbe.Domain;
using IsotopeProbe.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace IsotopeProbe.Tests;

public sealed class PersistenceModelTests
{
    [Fact]
    public void Model_MapsFieldsAndTracksFailedExecutionWithFindings()
    {
        var options = new DbContextOptionsBuilder<IsotopeProbeDbContext>()
            .UseNpgsql("Host=localhost;Database=model_test")
            .Options;
        using var db = new IsotopeProbeDbContext(options);
        var executionType = db.Model.FindEntityType(typeof(ScanExecution))!;
        var findingType = db.Model.FindEntityType(typeof(Finding))!;

        Assert.Equal("scan_executions", executionType.GetTableName());
        Assert.Equal("findings", findingType.GetTableName());
        Assert.Null(executionType.FindProperty(nameof(ScanExecution.Succeeded)));
        Assert.Null(executionType.FindProperty(nameof(ScanExecution.Duration)));
        Assert.Equal(ValueGenerated.OnAdd, executionType.FindProperty("Id")!.ValueGenerated);
        Assert.Equal(ValueGenerated.OnAdd, findingType.FindProperty("Id")!.ValueGenerated);
        Assert.Equal("jsonb", findingType.FindProperty(nameof(Finding.RawJson))!.GetColumnType());
        Assert.Equal("text[]", findingType.FindProperty(nameof(Finding.Authors))!.GetColumnType());
        Assert.Equal("text[]", findingType.FindProperty(nameof(Finding.Tags))!.GetColumnType());
        foreach (var property in typeof(Finding).GetProperties().Where(p => p.Name != nameof(Finding.ScanExecution)))
        {
            Assert.NotNull(findingType.FindProperty(property.Name));
        }

        var finding = new Finding
        {
            TemplateId = "test", Name = "Test", Severity = "info", MatchedAt = "http://localhost",
            RawJson = "{}", Authors = ["author"], Tags = ["tag"]
        };
        var execution = new ScanExecution { ExitCode = 2, Findings = [finding] };
        db.ScanExecutions.Add(execution);

        Assert.False(execution.Succeeded);
        Assert.Equal(EntityState.Added, db.Entry(execution).State);
        Assert.Equal(EntityState.Added, db.Entry(finding).State);
        Assert.Same(execution, finding.ScanExecution);
        Assert.Equal(db.Entry(execution).Property(x => x.Id).CurrentValue,
            db.Entry(finding).Property(x => x.ScanExecutionId).CurrentValue);
    }
}
