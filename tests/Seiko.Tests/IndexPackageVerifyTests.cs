using System.Text.Json;
using Xunit;

namespace Seiko.Tests;

// A maintainer runs this before submitting; the file mode checks the same
// rules as CI but must not require an index.json around the file.
public sealed class IndexPackageVerifyTests
{
    private static string WritePackage(Fixture fixture, SeiIndex index, string fileName = "sei-index.json")
    {
        var path = Path.Combine(fixture.Root, fileName);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(index, SeikoJsonContext.Default.SeiIndex));
        return path;
    }

    private static SeiIndex Package(string name = "hello")
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        return JsonSerializer.Deserialize<SeiIndex>(
            File.ReadAllBytes(Path.Combine(fixture.SourceDirectory, "sei", name + ".json")),
            SeikoJsonContext.Default.SeiIndex)!;
    }

    [Fact]
    public void VerifyPath_AcceptsAWellFormedPackageFile()
    {
        using var fixture = new Fixture();
        var path = WritePackage(fixture, Package());

        var result = IndexVerifier.VerifyPath(path);

        Assert.Empty(result.Findings);
        Assert.Equal(1, result.Sei);
    }

    [Fact]
    public void VerifyPath_RejectsSideFields()
    {
        using var fixture = new Fixture();
        var index = Package();
        index.Upstream = new UpstreamInfo
        {
            Url = "https://maintainer.invalid/sei-index.json",
            Sha256 = new string('a', 64),
        };
        index.Maintainers = ["github:someone"];
        index.Versions[0].SourceYanked = true;
        var path = WritePackage(fixture, index);

        var result = IndexVerifier.VerifyPath(path);

        Assert.Equal(2, result.Findings.Count(finding => finding.Code == "sei-side-field"));
        Assert.Contains(result.Findings, finding =>
            finding.Message.Contains("upstream", StringComparison.Ordinal));
        Assert.Contains(result.Findings, finding =>
            finding.Message.Contains("source*", StringComparison.Ordinal));
        Assert.True(result.Errors > 0);
    }

    [Fact]
    public void VerifyPath_WarnsOnTheWrongFileName()
    {
        using var fixture = new Fixture();
        var path = WritePackage(fixture, Package(), "hello.json");

        var result = IndexVerifier.VerifyPath(path);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("sei-filename", finding.Code);
        Assert.Equal("warning", finding.Severity);
    }

    [Fact]
    public void VerifyPath_RequiresProjectFields()
    {
        using var fixture = new Fixture();
        var index = Package();
        index.Project = null;
        var path = WritePackage(fixture, index);

        var result = IndexVerifier.VerifyPath(path);

        Assert.Contains(result.Findings, finding => finding.Code == "project-missing");
    }

    [Fact]
    public void VerifyPath_RejectsAnInvalidLicenseAndUrl()
    {
        using var fixture = new Fixture();
        var index = Package();
        index.Project = new ProjectMetadata
        {
            Description = "d",
            Authors = ["a"],
            License = "MIT AND",
            ProjectUrl = "http://insecure.invalid",
        };
        var path = WritePackage(fixture, index);

        var result = IndexVerifier.VerifyPath(path);

        Assert.Contains(result.Findings, finding => finding.Code == "project-license");
        Assert.Contains(result.Findings, finding => finding.Code == "project-url");
    }

    [Fact]
    public void VerifyPath_StillValidatesTheDirectory()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.WriteConfig("win-x64");

        var result = IndexVerifier.VerifyPath(fixture.SourceDirectory);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void VerifyPath_ReportsAMissingPathAsADirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nope");

        var result = IndexVerifier.VerifyPath(path);

        Assert.Contains(result.Findings, finding => finding.Code == "index-missing");
    }
}
