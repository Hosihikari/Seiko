using System.Text.Json;
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
    public void Sync_BackfillsMissingDigests()
    {
        using var fixture = NewFixture();
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var path = DigestPath(fixture);
        File.Delete(path);

        client.Sync(fixture.Root, fixture.StoreDirectory);

        Assert.True(File.Exists(path));
        Assert.Contains(ReadDigests(fixture).Files, file => file.Path == "hello.txt");
    }

    [Fact]
    public void Sync_RejectsCorruptStorePayload()
    {
        using var fixture = NewFixture();
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var storeFile = Path.Combine(fixture.StoreDirectory, ArtifactSha(fixture), "hello.txt");
        File.WriteAllText(storeFile, "HELLO");
        File.Delete(Path.Combine(fixture.Root, "hello.txt"));

        var error = Assert.Throws<SeikoException>(
            () => client.Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("checksum", error.Code);
        Assert.Contains("corrupted", error.Message);
    }

    [Fact]
    public void Sync_RejectsUnlistedStoreFile()
    {
        using var fixture = NewFixture();
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var directory = Path.Combine(fixture.StoreDirectory, ArtifactSha(fixture));
        File.WriteAllText(Path.Combine(directory, "injected.txt"), "injected");

        var error = Assert.Throws<SeikoException>(
            () => client.Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("checksum", error.Code);
        Assert.Contains("corrupted", error.Message);
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
