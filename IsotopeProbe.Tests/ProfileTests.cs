using IsotopeProbe.Profiles;
using IsotopeProbe.Comparisons;
using IsotopeProbe.Domain;

namespace IsotopeProbe.Tests;

public sealed class ProfileTests : IDisposable
{
    public const string Template = """
        id: example
        info:
          name: Example
          author: test
          severity: info
        http:
          - method: GET
            path:
              - "{{BaseURL}}/.git/config"
            matchers:
              - type: status
                status: [200]
        """;
    private readonly string root = Path.Combine(Path.GetTempPath(), "profiles-test-" + Guid.NewGuid().ToString("N"));
    public ProfileTests() { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "a.yaml"), Template); }
    private ProfileDefinition Definition() => new("test", "1", "Test", "Test profile", true, root, ["a.yaml"], [], null);
    private ProfileCatalog Catalog(params ProfileDefinition[] definitions) => new(new(Path.Combine(root, "store"), definitions.Length == 0 ? [Definition()] : definitions), _ => { });
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void ValidatesDefinitionsExclusionsDeduplicationAndEmptySelections()
    {
        Assert.Throws<ArgumentException>(() => Catalog(Definition(), Definition()));
        Assert.Throws<ArgumentException>(() => Catalog(Definition() with { Files = ["../outside.yaml"] }));
        Assert.Throws<ArgumentException>(() => Catalog(Definition() with { Exclusions = ["a.yaml"] }).Prepare("test"));
        Assert.Throws<ArgumentException>(() => Catalog(Definition() with { Enabled = false }).GetPrepared("test"));
        File.Copy(Path.Combine(root, "a.yaml"), Path.Combine(root, "b.yaml"));
        var prepared = Catalog(Definition() with { Files = ["a.yaml", "b.yaml", "a.yaml"] }).Prepare("test");
        Assert.Single(prepared.Manifest.Templates);
    }
    [Fact]
    public void HashIsDeterministicAndVersionsCannotChangeSilently()
    {
        var catalog = Catalog(); var prepared = catalog.Prepare("test");
        Assert.Equal(prepared, catalog.Prepare("test"), new PreparedComparer());
        Assert.Equal(prepared.Hash, ProfileCatalog.ManifestHash(prepared.Manifest with { CreatedAt = DateTimeOffset.UtcNow.AddDays(1) }));
        File.AppendAllText(Path.Combine(root, "a.yaml"), "\n# updated");
        Assert.Throws<ArgumentException>(() => catalog.Prepare("test"));
        Assert.NotEqual(prepared.Hash, Catalog(Definition() with { Version = "2" }).Prepare("test").Hash);
        Assert.Throws<ArgumentException>(() => Catalog(Definition() with { Description = "changed" }).GetPrepared("test"));
        var changed = prepared.Manifest with { Templates = [prepared.Manifest.Templates[0] with { Hash = new string('0', 64) }] };
        Assert.NotEqual(prepared.Hash, ProfileCatalog.ManifestHash(changed));
    }
    private sealed class PreparedComparer : IEqualityComparer<PreparedProfile>
    {
        public bool Equals(PreparedProfile? x, PreparedProfile? y) => x?.Hash == y?.Hash;
        public int GetHashCode(PreparedProfile x) => x.Hash.GetHashCode();
    }
    [Fact]
    public async Task ConcurrentPreparationPublishesCompleteSnapshots()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => Catalog().Prepare("test"))));
        Assert.Single(results.Select(x => x.Hash).Distinct());
        Assert.Single(Catalog().Verify(results[0].Hash, "test", "1"));
        Assert.Empty(Directory.GetDirectories(Path.Combine(root, "store"), ".preparing-*"));
    }
    [Fact]
    public async Task ConflictingConcurrentPreparationCannotRebindVersion()
    {
        using var staged = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = new ProfileCatalog(new(Path.Combine(root, "store"), [Definition()]), _ =>
        {
            staged.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        });
        var attempt = Task.Run(() => Assert.Throws<ArgumentException>(() => first.Prepare("test")));
        Assert.True(staged.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            File.AppendAllText(Path.Combine(root, "a.yaml"), "\n# concurrently updated");
            var winner = Catalog().Prepare("test");
            release.Set();
            await attempt;
            Assert.Equal(winner.Hash, Catalog().GetPrepared("test").Hash);
            Assert.Single(Catalog().Verify(winner.Hash, "test", "1"));
        }
        finally { release.Set(); }
    }
    [Fact]
    public void CapturedContentSurvivesSourceUpdateAndTamperingFails()
    {
        var catalog = Catalog(); var prepared = catalog.Prepare("test");
        var execution = new ScanExecution(); prepared.Capture(execution);
        File.WriteAllText(Path.Combine(root, "a.yaml"), "changed");
        var paths = catalog.Verify(execution.SnapshotHash!, execution.ProfileId, execution.ProfileVersion);
        Assert.Equal(Template, File.ReadAllText(paths[0]));
        File.AppendAllText(paths[0], "tampered");
        Assert.Throws<ArgumentException>(() => catalog.Verify(prepared.Hash, "test", "1"));
        File.Delete(paths[0]);
        Assert.Throws<ArgumentException>(() => catalog.Verify(prepared.Hash, "test", "1"));
    }
    [Theory]
    [InlineData("\ncode: []")]
    [InlineData("\nheadless: []")]
    [InlineData("\nvariables:\n  asset: '{{read_file(\"secret\")}}'")]
    public void UnsupportedDependenciesAndProtocolsRejected(string extra) =>
        Assert.Throws<ArgumentException>(() => ProfileCatalog.ValidateTemplate(Template + extra));
    [Fact]
    public void ExternalPayloadAndMissingFilesRejected()
    {
        Assert.Throws<ArgumentException>(() => ProfileCatalog.ValidateTemplate(Template + "\n    payloads:\n      paths: missing.txt"));
        Assert.Throws<FileNotFoundException>(() => Catalog(Definition() with { Files = ["missing.yaml"] }).Prepare("test"));
        File.WriteAllText(Path.Combine(root, "b.yaml"), Template + "\n# differs");
        Assert.Throws<ArgumentException>(() => Catalog(Definition() with { Files = ["a.yaml", "b.yaml"] }).Prepare("test"));
    }
    [Fact]
    public void CliOptionsAndProcessIsolation()
    {
        Assert.Equal("test", Cli.ScanOptions.Parse(["http://localhost", "--profile", "test"]).ProfileId);
        Assert.Throws<ArgumentException>(() => Cli.ScanOptions.Parse(["--profile", "test", "http://localhost", "-templatepath", "x"]));
        var start = Nuclei.NucleiRunner.CreateProfileStartInfo("http://localhost", ["/trusted/with spaces/a.yaml"], root);
        Assert.Contains("/trusted/with spaces/a.yaml", start.ArgumentList);
        Assert.Contains("-duc", start.ArgumentList); Assert.Contains("-ni", start.ArgumentList);
        Assert.Equal(root, start.Environment["HOME"]);
    }
    [Fact]
    public void ComparisonReportsUnknownAndChangedProvenance()
    {
        var old = new ComparisonExecution(1, 1, "http://localhost", null, null, ScanStatus.Succeeded, null, null, null);
        Assert.Contains("unknown", OwnedScanComparisonService.ProvenanceWarning(old, old));
        var known = old with { ProfileId = "test", ProfileVersion = "1", SnapshotHash = "hash", NucleiVersion = "v3" };
        Assert.Contains("Matching recorded template content", OwnedScanComparisonService.ProvenanceWarning(known, known));
        var warning = OwnedScanComparisonService.ProvenanceWarning(known, known with { ProfileVersion = "2", SnapshotHash = "changed", NucleiVersion = "v4" });
        Assert.Contains("versions differ", warning); Assert.Contains("hashes differ", warning); Assert.Contains("Nuclei versions differ", warning);
    }
}

internal static class TestProfiles
{
    private static readonly Lazy<ProfileCatalog> instance = new(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "isotope-test-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "test.yaml"), ProfileTests.Template);
        var catalog = new ProfileCatalog(new(Path.Combine(root, "store"), [
            new("standard-website", "1", "Standard website scan", "Test", true, root, ["test.yaml"], [], null),
            new("disabled", "1", "Disabled", "Test", false, root, ["test.yaml"], [], null)]), _ => { });
        catalog.Prepare("standard-website");
        return catalog;
    });
    public static ProfileCatalog Catalog => instance.Value;
}
