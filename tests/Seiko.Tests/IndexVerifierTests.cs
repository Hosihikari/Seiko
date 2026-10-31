using System.Text.Json;
using Xunit;

namespace Seiko.Tests;

public sealed class IndexVerifierTests
{
    [Fact]
    public void Verify_AcceptsValidIndex()
    {
        using var fixture = NewFixture();

        var result = IndexVerifier.Verify(fixture.SourceDirectory);

        Assert.Empty(result.Findings);
        Assert.Equal(1, result.Sei);
        Assert.Equal(1, result.Versions);
        Assert.Equal(1, result.Artifacts);
        Assert.Equal(0, result.CheckedArtifacts);
    }

    [Fact]
    public void Verify_AcceptsUnlistedPackageAndAdvisory()
    {
        using var fixture = NewFixture();
        var sei = ReadSei(fixture, "hello");
        sei.Unlisted = true;
        sei.Deprecated = "use hello2 instead";
        sei.Versions[0].Advisory = new AdvisoryInfo
        {
            Severity = "critical",
            Url = "https://example.com/advisories/1",
        };
        WriteSei(fixture, "hello", sei);

        var result = IndexVerifier.Verify(fixture.SourceDirectory);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Verify_AcceptsAdvisorySeverityWithWhitespace()
    {
        using var fixture = NewFixture();
        var sei = ReadSei(fixture, "hello");
        sei.Versions[0].Advisory = new AdvisoryInfo { Severity = " CRITICAL " };
        WriteSei(fixture, "hello", sei);

        var result = IndexVerifier.Verify(fixture.SourceDirectory);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Verify_RejectsInvalidAdvisory()
    {
        using var fixture = NewFixture();
        var sei = ReadSei(fixture, "hello");
        sei.Versions[0].Advisory = new AdvisoryInfo
        {
            Severity = "urgent",
            Url = "not a url",
        };
        WriteSei(fixture, "hello", sei);

        var result = IndexVerifier.Verify(fixture.SourceDirectory);

        Assert.Contains(result.Findings, finding => finding.Code == "advisory-severity");
        Assert.Contains(result.Findings, finding => finding.Code == "advisory-url");
    }

    [Fact]
    public void Verify_RejectsDuplicateNormalizedArchivePaths()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files =
                    {
                        ["hello.txt"] = "one",
                        ["./hello.txt"] = "two",
                    },
                },
            ],
        });
        fixture.WriteConfig("win-x64");

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding => finding.Code == "artifact-duplicate");
    }

    [Fact]
    public void Verify_CheckArtifactsValidatesManifest()
    {
        using var fixture = NewFixture();

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Empty(result.Findings);
        Assert.Equal(1, result.CheckedArtifacts);
    }

    [Fact]
    public void Verify_RejectsPrivilegedModes()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["hello.txt"] = "hello" },
                    Modes = new Dictionary<string, string> { ["hello.txt"] = "04755" },
                },
            ],
        });
        fixture.WriteConfig("win-x64");

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding => finding.Code == "manifest-mode");
    }

    [Fact]
    public void Verify_CheckArtifactsDownloadsHttpArtifacts()
    {
        using var fixture = NewFixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        var sei = ReadSei(fixture, "hello");
        var source = sei.Versions[0].Artifacts[0].Source;
        source.Url = server.BaseUrl + "artifacts/" + Path.GetFileName(source.Url);
        WriteSei(fixture, "hello", sei);

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Empty(result.Findings);
        Assert.Equal(1, result.CheckedArtifacts);
    }

    [Fact]
    public void Verify_LogRedactsSignedArtifactUrls()
    {
        using var fixture = NewFixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        var sei = ReadSei(fixture, "hello");
        var source = sei.Versions[0].Artifacts[0].Source;
        source.Url = server.BaseUrl + "artifacts/" + Path.GetFileName(source.Url) + "?sig=REREVIEW_SENTINEL";
        WriteSei(fixture, "hello", sei);
        var logs = new List<string>();

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true, log: logs.Add);

        Assert.Empty(result.Findings);
        Assert.DoesNotContain(logs, line => line.Contains("REREVIEW_SENTINEL", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_ReportsMissingAndUnlistedSei()
    {
        using var fixture = NewFixture();
        var index = ReadIndex(fixture);
        index.Sei = ["hello", "missing"];
        WriteIndex(fixture, index);
        var extra = ReadSei(fixture, "hello");
        extra.Name = "extra";
        WriteSei(fixture, "extra", extra);

        var result = IndexVerifier.Verify(fixture.SourceDirectory);

        Assert.Contains(result.Findings, finding => finding.Code == "sei-missing");
        Assert.Contains(result.Findings, finding => finding.Code == "sei-not-listed");
    }

    [Fact]
    public void Verify_WarnsWhenListIsMissing()
    {
        using var fixture = NewFixture();
        var index = ReadIndex(fixture);
        index.Sei = null;
        WriteIndex(fixture, index);

        var result = IndexVerifier.Verify(fixture.SourceDirectory);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("warning", finding.Severity);
        Assert.Equal("index-list-missing", finding.Code);
    }

    [Fact]
    public void Verify_RejectsInvalidName()
    {
        using var fixture = NewFixture();
        var index = ReadIndex(fixture);
        index.Sei = ["../hello"];
        WriteIndex(fixture, index);

        var result = IndexVerifier.Verify(fixture.SourceDirectory);

        Assert.Contains(result.Findings, finding => finding.Code == "sei-name");
    }

    [Fact]
    public void Sync_RejectsUnsafeDependencyName()
    {
        using var fixture = new Fixture();
        fixture.AddSei("app", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("../../escape", null)],
            Artifacts = [new ArtifactFixture { Files = { ["app.txt"] = "app" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("app"));

        var exception = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("resolve", exception.Code);
        Assert.Contains("invalid sei name", exception.Message);
    }

    [Fact]
    public void Verify_RejectsDuplicateVersions()
    {
        using var fixture = NewFixture();
        var sei = ReadSei(fixture, "hello");
        sei.Versions.Add(new SeiVersionEntry { Version = "1.0" });
        WriteSei(fixture, "hello", sei);

        var result = IndexVerifier.Verify(fixture.SourceDirectory);

        Assert.Contains(result.Findings, finding => finding.Code == "version-duplicate");
    }

    [Fact]
    public void Verify_RejectsAmbiguousArtifacts()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture { Files = { ["a.txt"] = "a" } },
                new ArtifactFixture { Files = { ["b.txt"] = "b" } },
            ],
        });
        fixture.WriteConfig("win-x64");

        var result = IndexVerifier.Verify(fixture.SourceDirectory);

        Assert.Contains(result.Findings, finding => finding.Code == "artifact-ambiguous");
    }

    [Fact]
    public void Verify_RejectsInvalidArtifactFields()
    {
        using var fixture = NewFixture();
        var sei = ReadSei(fixture, "hello");
        var source = sei.Versions[0].Artifacts[0].Source;
        source.Url = "../escape.zip";
        source.Sha256 = "ABC";
        source.Size = 0;
        WriteSei(fixture, "hello", sei);

        var result = IndexVerifier.Verify(fixture.SourceDirectory);

        Assert.Contains(result.Findings, finding => finding.Code == "artifact-url");
        Assert.Contains(result.Findings, finding => finding.Code == "artifact-sha256");
        Assert.Contains(result.Findings, finding => finding.Code == "artifact-size");
    }

    [Fact]
    public void Verify_CheckArtifactsReportsChecksumMismatch()
    {
        using var fixture = NewFixture();
        fixture.CorruptChecksum("hello");

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding => finding.Code == "artifact-checksum");
    }

    [Fact]
    public void Verify_CheckArtifactsReportsManifestMismatch()
    {
        using var fixture = NewFixture();
        var sei = ReadSei(fixture, "hello");
        sei.Versions[0].Version = "2.0.0";
        WriteSei(fixture, "hello", sei);

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding => finding.Code == "manifest-mismatch");
    }

    [Fact]
    public void InitTemplate_LoadsTheOfficialSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "seiko-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "seiko.conf"), InitTemplate.Create() + "\n");

            var config = ConfigLoader.Load(root);

            var source = Assert.Single(config.Sources);
            Assert.Equal(InitTemplate.OfficialSourceId, source.Id);
            Assert.Equal(InitTemplate.OfficialIndex, source.Index);
            Assert.Empty(config.Sei);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Fixture NewFixture()
    {
        var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64");
        return fixture;
    }

    private static IndexRoot ReadIndex(Fixture fixture)
        => JsonSerializer.Deserialize(
            File.ReadAllBytes(Path.Combine(fixture.SourceDirectory, "index.json")),
            SeikoJsonContext.Default.IndexRoot)!;

    private static void WriteIndex(Fixture fixture, IndexRoot index)
        => File.WriteAllBytes(
            Path.Combine(fixture.SourceDirectory, "index.json"),
            JsonSerializer.SerializeToUtf8Bytes(index, SeikoJsonContext.Default.IndexRoot));

    private static SeiIndex ReadSei(Fixture fixture, string name)
        => JsonSerializer.Deserialize(
            File.ReadAllBytes(Path.Combine(fixture.SourceDirectory, "sei", name + ".json")),
            SeikoJsonContext.Default.SeiIndex)!;

    private static void WriteSei(Fixture fixture, string name, SeiIndex sei)
        => File.WriteAllBytes(
            Path.Combine(fixture.SourceDirectory, "sei", name + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(sei, SeikoJsonContext.Default.SeiIndex));
}
