using IsotopeProbe.Cli;
using IsotopeProbe.Nuclei;

namespace IsotopeProbe.Tests;

public sealed class ScanOptionsTests
{
    [Fact]
    public void Parse_TargetOnly_LeavesTemplatesUnspecified()
    {
        Assert.Equal(new ScanOptions("http://localhost:8085", null),
            ScanOptions.Parse(["http://localhost:8085"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_TemplateOption_CanPrecedeOrFollowTarget(bool optionFirst)
    {
        string[] args = optionFirst
            ? ["-templatepath", "~/Templates/sanity/", "http://localhost:8085"]
            : ["http://localhost:8085", "-templatepath", "~/Templates/sanity/"];

        Assert.Equal(new ScanOptions("http://localhost:8085", "~/Templates/sanity/"),
            ScanOptions.Parse(args));
    }

    [Theory]
    [InlineData()]
    [InlineData("target", "-templatepath")]
    [InlineData("target", "-templatepath", " ")]
    [InlineData("target", "-templatepath", "-unknown")]
    [InlineData("target", "-templatepath", "one", "-templatepath", "two")]
    [InlineData("-templatepath", "path")]
    [InlineData("target", "extra")]
    [InlineData("target", "-unknown")]
    public void Parse_InvalidArguments_Throws(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => ScanOptions.Parse(args));
    }

    [Theory]
    [InlineData("/tmp/templates with spaces/")]
    [InlineData("relative/templates")]
    [InlineData("~/Templates/sanity/")]
    public void CreateStartInfo_MapsTemplatePathToSingleNucleiArgument(string path)
    {
        var options = ScanOptions.Parse(["http://localhost:8085", "-templatepath", path]);
        var startInfo = NucleiRunner.CreateStartInfo(options.Target, options.TemplatePath);
        var expectedPath = path.StartsWith("~/")
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;

        Assert.Equal("nuclei", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(new[] { "-u", options.Target, "-t", expectedPath, "-jsonl", "-silent" },
            startInfo.ArgumentList);
    }

    [Fact]
    public void CreateStartInfo_WithoutTemplatePath_OmitsTemplateFlag()
    {
        Assert.Equal(new[] { "-u", "target", "-jsonl", "-silent" },
            NucleiRunner.CreateStartInfo("target", null).ArgumentList);
    }
}
