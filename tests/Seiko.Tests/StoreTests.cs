using Xunit;

namespace Seiko.Tests;

public sealed class StoreTests
{
    [Fact]
    public void Store_VerifiesFreshlyExtractedFiles()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        var lockFile = new SeikoClient().ReadLock(fixture.Root)!;
        var sha = Assert.Single(lockFile.Sei).Artifact.Sha256;
        var meta = Path.Combine(fixture.StoreDirectory, ".meta", sha + ".json");
        Assert.True(File.Exists(meta));
        var state = System.Text.Json.JsonSerializer.Deserialize(
            File.ReadAllBytes(meta),
            SeikoJsonContext.Default.StoreDigestsState)!;
        Assert.Equal(sha, state.Artifact);
        Assert.All(state.Files, file =>
            Assert.Equal(Hashing.Sha256File(
                Path.Combine(fixture.StoreDirectory, sha, file.Path)), file.Sha256));
    }

    [Fact]
    public void Store_RepairsACorruptedArtifact()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var digest = Assert.Single(client.ReadLock(fixture.Root)!.Sei).Artifact.Sha256;
        var storedFile = Path.Combine(fixture.StoreDirectory, digest, "hello.txt");
        File.WriteAllText(storedFile, "tampered");

        client.Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("hello", File.ReadAllText(storedFile));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Sync_RejectsUnsafeStoreEntryNames()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["foo:bar"] = "x" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("io", error.Code);
        Assert.Contains("unsafe", error.Message);
    }

    [Fact]
    public async Task Store_WaitsForTheArtifactLockBeforeReading()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var digest = Assert.Single(client.ReadLock(fixture.Root)!.Sei).Artifact.Sha256;
        using var held = ProcessLock.Acquire(
            Path.Combine(fixture.StoreDirectory, ".locks", digest + ".lock"),
            "test artifact");
        var store = new Store(fixture.StoreDirectory);

        var read = Task.Run(() => store.TryGet(digest));
        await Task.Delay(200);
        Assert.False(read.IsCompleted);

        held.Dispose();
        Assert.NotNull(await read);
    }
}
