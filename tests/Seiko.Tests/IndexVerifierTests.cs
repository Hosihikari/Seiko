using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public void Verify_RejectsAnOldIndexSchema()
    {
        using var fixture = NewFixture();
        var path = Path.Combine(fixture.SourceDirectory, "index.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1"));

        var result = IndexVerifier.Verify(fixture.SourceDirectory);

        Assert.Contains(result.Findings, finding =>
            finding.Code == "index-schema" && finding.Message.Contains("unsupported index schemaVersion"));
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
        fixture.RefreshCatalog();

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
        fixture.RefreshCatalog();

        var result = IndexVerifier.Verify(fixture.SourceDirectory);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Verify_RejectsNullIndexDependencyList()
    {
        using var fixture = NewFixture();
        var path = Path.Combine(fixture.SourceDirectory, "sei", "hello.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var version in node["versions"]!.AsArray())
        {
            version!.AsObject()["dependencies"] = null;
        }

        File.WriteAllText(path, node.ToJsonString());

        var result = IndexVerifier.Verify(fixture.SourceDirectory);

        Assert.Contains(result.Findings, finding => finding.Code == "dependency-list");
    }

    [Fact]
    public void Verify_RejectsNullArtifactDependencyList()
    {
        using var fixture = NewFixture();
        var path = Path.Combine(fixture.SourceDirectory, "sei", "hello.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var version in node["versions"]!.AsArray())
        {
            foreach (var artifact in version!["artifacts"]!.AsArray())
            {
                artifact!.AsObject()["dependencies"] = null;
            }
        }

        File.WriteAllText(path, node.ToJsonString());

        var result = IndexVerifier.Verify(fixture.SourceDirectory);

        Assert.Contains(result.Findings, finding => finding.Code == "artifact-dependency-list");
    }

    [Fact]
    public void Verify_RejectsNullManifestDependencyList()
    {
        using var fixture = NewFixture();
        MakeManifestDependenciesNull(fixture);

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(
            result.Findings,
            finding => finding.Code == "manifest-mismatch"
                && finding.Message.Contains("dependency", StringComparison.Ordinal));
    }

    [Fact]
    public void Sync_RejectsNullManifestDependencyList()
    {
        using var fixture = NewFixture();
        MakeManifestDependenciesNull(fixture);
        fixture.WriteConfig("win-x64", new SpecFixture("hello"));
        var client = new SeikoClient();

        var error = Assert.Throws<SeikoException>(
            () => client.Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("io", error.Code);
        Assert.Contains("dependency", error.Message, StringComparison.Ordinal);
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
        fixture.RefreshCatalog();

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
    public void Verify_RejectsCaseEquivalentArchivePaths()
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
                        ["A.txt"] = "a",
                        ["a.txt"] = "b",
                    },
                },
            ],
        });
        fixture.WriteConfig("win-x64");

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding => finding.Code == "artifact-path");
    }

    [Fact]
    public void Verify_RejectsNormalizationEquivalentArchivePaths()
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
                        ["é.txt"] = "a",
                        ["é.txt"] = "b",
                    },
                },
            ],
        });
        fixture.WriteConfig("win-x64");

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding => finding.Code == "artifact-path");
    }

    [Fact]
    public void Verify_RejectsCaseEquivalentFileAndDirectoryPrefixes()
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
                        ["Tool"] = "a",
                        ["tool/data"] = "b",
                    },
                },
            ],
        });
        fixture.WriteConfig("win-x64");

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding => finding.Code == "artifact-path");
    }

    [Fact]
    public void Verify_RejectsUnsafeArchivePaths()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["foo:bar"] = "x" } }],
        });
        fixture.WriteConfig("win-x64");

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding => finding.Code == "artifact-path");
    }

    [Fact]
    public void Verify_RejectsOverlongArchivePaths()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { [new string('a', ArchiveLimits.MaxEntryNameLength + 1)] = "x" },
                },
            ],
        });
        fixture.WriteConfig("win-x64");

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding => finding.Code == "artifact-path");
    }

    [Fact]
    public void Verify_RejectsTooDeepArchivePaths()
    {
        using var fixture = new Fixture();
        var directory = string.Join(
            '/',
            Enumerable.Repeat("a", ArchiveLimits.MaxEntryNameDepth));
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { [directory + "/x"] = "x" },
                },
            ],
        });
        fixture.WriteConfig("win-x64");

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding => finding.Code == "artifact-path");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Verify_RejectsFileAndDirectoryPrefixConflicts(bool reversed)
    {
        using var fixture = new Fixture();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (reversed)
        {
            files["tool/x"] = "x";
            files["tool"] = "tool";
        }
        else
        {
            files["tool"] = "tool";
            files["tool/x"] = "x";
        }

        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = files }],
        });
        fixture.WriteConfig("win-x64");

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding => finding.Code == "artifact-path");
    }

    [Theory]
    [InlineData("https://")]
    [InlineData("https:///artifact.zip")]
    [InlineData("ftp://example.invalid/artifact.zip")]
    public void Verify_RejectsRemoteArtifactUrlsWithoutAValidHost(string url)
    {
        using var fixture = NewFixture();
        var sei = ReadSei(fixture, "hello");
        sei.Versions[0].Artifacts[0].Source.Url = url;
        WriteSei(fixture, "hello", sei);

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding => finding.Code == "artifact-url");
        Assert.Equal(0, result.CheckedArtifacts);
        Assert.DoesNotContain(result.Findings, finding => finding.Code == "internal");
    }

    [Theory]
    [InlineData("ftp://example.invalid/a.zip?token=REREVIEW_SENTINEL")]
    [InlineData("https://?token=REREVIEW_SENTINEL")]
    [InlineData("../a.zip?token=REREVIEW_SENTINEL")]
    public void Verify_DoesNotEchoInvalidArtifactUrlCredentials(string url)
    {
        using var fixture = NewFixture();
        var sei = ReadSei(fixture, "hello");
        sei.Versions[0].Artifacts[0].Source.Url = url;
        WriteSei(fixture, "hello", sei);
        fixture.RefreshCatalog();

        var result = IndexVerifier.Verify(fixture.SourceDirectory);

        var finding = Assert.Single(result.Findings, item => item.Code == "artifact-url");
        Assert.DoesNotContain("REREVIEW_SENTINEL", finding.Message);
    }

    [Fact]
    public void Verify_RejectsAdvisoryUrlsWithoutAHost()
    {
        using var fixture = NewFixture();
        var sei = ReadSei(fixture, "hello");
        sei.Versions[0].Advisory = new AdvisoryInfo { Severity = "low", Url = "https://" };
        WriteSei(fixture, "hello", sei);
        fixture.RefreshCatalog();

        var result = IndexVerifier.Verify(fixture.SourceDirectory);

        Assert.Contains(result.Findings, finding => finding.Code == "advisory-url");
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
    public void Verify_RejectsInvalidPrerequisiteRange()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Prerequisites = [new DepFixture("marker", "not-a-range")],
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64");

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding => finding.Code == "prerequisite-range");
    }

    [Fact]
    public void Verify_RejectsArtifactSizesBeyondTheExtractionLimit()
    {
        using var fixture = NewFixture();
        var sei = ReadSei(fixture, "hello");
        sei.Versions[0].Artifacts[0].Source.Size = ArchiveLimits.MaxTotalBytes + 1;
        WriteSei(fixture, "hello", sei);

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding => finding.Code == "artifact-size");
        Assert.Equal(0, result.CheckedArtifacts);
    }

    [Fact]
    public void Verify_CheckArtifactsRejectsOversizedLocalArtifacts()
    {
        using var fixture = NewFixture();
        var sei = ReadSei(fixture, "hello");
        sei.Versions[0].Artifacts[0].Source.Size = 1;
        WriteSei(fixture, "hello", sei);

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding => finding.Code == "artifact-size");
    }

    [Fact]
    public void Verify_CheckArtifactsRejectsOversizedHttpResponses()
    {
        using var fixture = NewFixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        var sei = ReadSei(fixture, "hello");
        var source = sei.Versions[0].Artifacts[0].Source;
        source.Url = server.BaseUrl + "artifacts/" + Path.GetFileName(source.Url);
        source.Size = 1;
        WriteSei(fixture, "hello", sei);

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding => finding.Code == "artifact-size");
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
    public void Verify_AcceptsExternalDeclarationsWithoutDownloading()
    {
        using var fixture = ExternalFixture(
            new ExternalFileFixture("vendor/server.bin", "https://vendor.invalid/server.bin", "data"));

        var result = IndexVerifier.Verify(fixture.SourceDirectory);

        Assert.Empty(result.Findings);
        Assert.Equal(0, result.CheckedArtifacts);
    }

    [Fact]
    public void Verify_RejectsExternalUrlWithUserinfo()
    {
        using var fixture = ExternalFixture(
            new ExternalFileFixture(
                "vendor/server.bin",
                "https://user:secret@vendor.invalid/server.bin",
                "data"));

        var result = IndexVerifier.Verify(fixture.SourceDirectory);

        Assert.Contains(result.Findings, finding =>
            finding.Code == "artifact-external" && finding.Message.Contains("userinfo"));
    }

    [Fact]
    public void Verify_RejectsExternalTargetInsideTheZip()
    {
        using var fixture = ExternalFixture(
            new ExternalFileFixture("hello.txt", "https://vendor.invalid/hello.txt", "data"));

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding =>
            finding.Code == "manifest-external" && finding.Message.Contains("also exists inside"));
    }

    [Fact]
    public void Verify_CheckArtifactsReportsForeignLocation()
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
        fixture.RefreshCatalog();

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
        fixture.RefreshCatalog();

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

    private static Fixture ExternalFixture(params ExternalFileFixture[] files)
    {
        var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["hello.txt"] = "hello" },
                    ExternalFiles = files.ToList(),
                },
            ],
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

    private static void MakeManifestDependenciesNull(Fixture fixture)
    {
        var zipPath = Assert.Single(Directory.GetFiles(
            Path.Combine(fixture.SourceDirectory, "artifacts"),
            "*.zip"));
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("sei.json")!;
            string text;
            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream))
            {
                text = reader.ReadToEnd();
            }

            entry.Delete();
            var replacement = archive.CreateEntry("sei.json");
            using var output = replacement.Open();
            using var writer = new StreamWriter(output);
            var node = JsonNode.Parse(text)!;
            node.AsObject()["dependencies"] = null;
            writer.Write(node.ToJsonString());
        }

        var sei = ReadSei(fixture, "hello");
        var source = sei.Versions[0].Artifacts[0].Source;
        source.Sha256 = Hashing.Sha256File(zipPath);
        source.Size = new FileInfo(zipPath).Length;
        WriteSei(fixture, "hello", sei);
    }

    private static SeiIndex ReadSei(Fixture fixture, string name)
        => JsonSerializer.Deserialize(
            File.ReadAllBytes(Path.Combine(fixture.SourceDirectory, "sei", name + ".json")),
            SeikoJsonContext.Default.SeiIndex)!;

    private static void WriteSei(Fixture fixture, string name, SeiIndex sei)
        => File.WriteAllBytes(
            Path.Combine(fixture.SourceDirectory, "sei", name + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(sei, SeikoJsonContext.Default.SeiIndex));
}
