using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Seiko.Tests;

public sealed class DeployLinkTests
{
    [Fact]
    public void FileLink_Create_LinksFilesOnTheSameVolume()
    {
        var directory = Path.Combine(Path.GetTempPath(), "seiko-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.txt");
            var destination = Path.Combine(directory, "destination.txt");
            File.WriteAllText(source, "content");

            var kind = FileLink.Create(source, destination);

            Assert.NotEqual(LinkKind.Copy, kind);
            Assert.Equal("content", File.ReadAllText(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Sync_WritesStoreDigests()
    {
        using var fixture = NewFixture();
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var state = ReadDigests(fixture);

        var digest = Assert.Single(state.Files, file => file.Path == "hello.txt");
        Assert.Equal(Hashing.Sha256File(Path.Combine(fixture.Root, "hello.txt")), digest.Sha256);
        Assert.Contains(state.Files, file => file.Path == "sei.json");
    }

    [Fact]
    public void Sync_RepairsTamperedCachedManifest()
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
                    Modes = new Dictionary<string, string> { ["hello.txt"] = "0644" },
                },
            ],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var manifestPath = Path.Combine(fixture.StoreDirectory, ArtifactSha(fixture), "sei.json");
        var manifest = JsonSerializer.Deserialize(
            File.ReadAllBytes(manifestPath),
            SeikoJsonContext.Default.SeiManifest)!;
        manifest.Modes!["hello.txt"] = "0777";
        File.WriteAllBytes(
            manifestPath,
            JsonSerializer.SerializeToUtf8Bytes(manifest, SeikoJsonContext.Default.SeiManifest));

        client.Sync(fixture.Root, fixture.StoreDirectory);

        var restored = JsonSerializer.Deserialize(
            File.ReadAllBytes(manifestPath),
            SeikoJsonContext.Default.SeiManifest)!;
        Assert.Equal("0644", restored.Modes!["hello.txt"]);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Sync_RepairsMissingDigestsAndPayload()
    {
        using var fixture = NewFixture();
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var path = DigestPath(fixture);
        var storeFile = Path.Combine(fixture.StoreDirectory, ArtifactSha(fixture), "hello.txt");
        File.Delete(path);
        File.Delete(storeFile);
        File.Delete(Path.Combine(fixture.Root, "hello.txt"));

        client.Sync(fixture.Root, fixture.StoreDirectory);

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(storeFile));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "hello.txt")));
        Assert.Contains(ReadDigests(fixture).Files, file => file.Path == "hello.txt");
    }

    [Fact]
    public void Sync_RepairsInvalidDigestPaths()
    {
        using var fixture = NewFixture();
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var digestPath = DigestPath(fixture);
        var state = JsonNode.Parse(File.ReadAllText(digestPath))!.AsObject();
        state["files"]!.AsArray().Add(new JsonObject
        {
            ["path"] = "bad\u0000path",
            ["sha256"] = new string('a', 64),
            ["size"] = 0,
        });
        File.WriteAllText(digestPath, state.ToJsonString());

        client.Sync(fixture.Root, fixture.StoreDirectory);

        Assert.DoesNotContain(ReadDigests(fixture).Files, file => file.Path.Contains('\0'));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void CacheVerify_DoesNotReadDigestPathsOutsideTheArtifact()
    {
        using var fixture = NewFixture();
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var outside = Path.Combine(fixture.StoreDirectory, "outside.txt");
        File.WriteAllText(outside, "outside");
        var digestPath = DigestPath(fixture);
        var state = JsonNode.Parse(File.ReadAllText(digestPath))!.AsObject();
        state["files"]!.AsArray().Add(new JsonObject
        {
            ["path"] = "../outside.txt",
            ["sha256"] = Hashing.Sha256File(outside),
            ["size"] = new FileInfo(outside).Length,
        });
        File.WriteAllText(digestPath, state.ToJsonString());

        var findings = client.CacheVerify(fixture.StoreDirectory);

        Assert.Contains(findings, finding => finding.Code == "cache-meta");
        Assert.DoesNotContain(findings, finding => finding.Code == "cache-corrupt");
    }

    [Fact]
    public void Sync_RepairsMalformedDigestEntries()
    {
        using var fixture = NewFixture();
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var digestPath = DigestPath(fixture);
        var state = JsonNode.Parse(File.ReadAllText(digestPath))!.AsObject();
        state["files"] = new JsonArray((JsonNode?)null);
        File.WriteAllText(digestPath, state.ToJsonString());

        client.Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Contains(ReadDigests(fixture).Files, file => file.Path == "hello.txt");
        Assert.Equal("hello", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Sync_RepairsCorruptStorePayload()
    {
        using var fixture = NewFixture();
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var storeFile = Path.Combine(fixture.StoreDirectory, ArtifactSha(fixture), "hello.txt");
        File.WriteAllText(storeFile, "HELLO");
        File.Delete(Path.Combine(fixture.Root, "hello.txt"));

        client.Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("hello", File.ReadAllText(storeFile));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Sync_RejectsPrivilegedModes()
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
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("deploy", error.Code);
        Assert.Contains("setuid", error.Message);
    }

    [Fact]
    public void Sync_RepairsMissingStorePayload()
    {
        using var fixture = NewFixture();
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var storeFile = Path.Combine(fixture.StoreDirectory, ArtifactSha(fixture), "hello.txt");
        File.Delete(storeFile);

        client.Sync(fixture.Root, fixture.StoreDirectory);

        Assert.True(File.Exists(storeFile));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Sync_RepairsUnlistedStoreFile()
    {
        using var fixture = NewFixture();
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var directory = Path.Combine(fixture.StoreDirectory, ArtifactSha(fixture));
        File.WriteAllText(Path.Combine(directory, "injected.txt"), "injected");

        client.Sync(fixture.Root, fixture.StoreDirectory);

        Assert.False(File.Exists(Path.Combine(directory, "injected.txt")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void CacheVerify_DetectsCorruptionAndUnlistedFiles()
    {
        using var fixture = NewFixture();
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var directory = Path.Combine(fixture.StoreDirectory, ArtifactSha(fixture));
        File.WriteAllText(Path.Combine(directory, "hello.txt"), "HELLO");
        File.WriteAllText(Path.Combine(directory, "extra.txt"), "extra");

        var findings = client.CacheVerify(fixture.StoreDirectory);

        Assert.Contains(findings, finding => finding.Code == "cache-corrupt");
        Assert.Contains(findings, finding => finding.Code == "cache-unlisted");
    }

    [Fact]
    public void CacheVerify_WarnsWithoutDigestRecord()
    {
        using var fixture = NewFixture();
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        File.Delete(DigestPath(fixture));

        var finding = Assert.Single(client.CacheVerify(fixture.StoreDirectory));

        Assert.Equal("warning", finding.Severity);
        Assert.Equal("cache-meta", finding.Code);
    }

    [Fact]
    public void CacheClean_RemovesOrphanDigests()
    {
        using var fixture = NewFixture();
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var orphan = Path.Combine(
            fixture.StoreDirectory,
            ".meta",
            new string('c', 64) + ".json");
        File.WriteAllText(orphan, "{}");

        client.CacheClean(fixture.Root, fixture.StoreDirectory);

        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(DigestPath(fixture)));
    }

    private static Fixture NewFixture()
    {
        var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        return fixture;
    }

    private static string ArtifactSha(Fixture fixture)
        => new SeikoClient().ReadLock(fixture.Root)!.Sei.Single().Artifact.Sha256;

    private static string DigestPath(Fixture fixture)
        => Path.Combine(fixture.StoreDirectory, ".meta", ArtifactSha(fixture) + ".json");

    private static StoreDigestsState ReadDigests(Fixture fixture)
        => JsonSerializer.Deserialize(
            File.ReadAllBytes(DigestPath(fixture)),
            SeikoJsonContext.Default.StoreDigestsState)!;
}
