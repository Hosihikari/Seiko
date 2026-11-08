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

        // Repair must be requested explicitly. The deployed copy may or may
        // not share the inode: reflink on macOS isolates it, so whether the
        // deployed bytes changed is not a property this test can rely on.
        client.Sync(fixture.Root, fixture.StoreDirectory, force: true);

        Assert.Equal("hello", File.ReadAllText(storedFile));
    }

    [Fact]
    public void Sync_BlocksALocallyEditedDeployedFile()
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
        var deployed = Path.Combine(fixture.Root, "hello.txt");
        // Detach from any hardlink so the store stays intact and only the
        // deployed entry carries the edit.
        var detached = deployed + ".edit";
        File.WriteAllText(detached, "edited");
        File.Move(detached, deployed, overwrite: true);

        var error = Assert.Throws<SeikoException>(
            () => client.Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("deploy", error.Code);
        Assert.Equal("edited", File.ReadAllText(deployed));
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
    public void Sync_ReportsAManifestWithoutAnArtifactObject()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var zipPath = Assert.Single(Directory.GetFiles(
            Path.Combine(fixture.SourceDirectory, "artifacts"),
            "*.zip"));
        using (var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Update))
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
            var node = System.Text.Json.Nodes.JsonNode.Parse(text)!;
            node.AsObject().Remove("artifact");
            writer.Write(node.ToJsonString());
        }

        var indexPath = Path.Combine(fixture.SourceDirectory, "sei", "hello.json");
        var sei = System.Text.Json.JsonSerializer.Deserialize(
            File.ReadAllBytes(indexPath),
            SeikoJsonContext.Default.SeiIndex)!;
        sei.Versions[0].Artifacts[0].Source.Sha256 = Hashing.Sha256File(zipPath);
        sei.Versions[0].Artifacts[0].Source.Size = new FileInfo(zipPath).Length;
        File.WriteAllBytes(
            indexPath,
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(sei, SeikoJsonContext.Default.SeiIndex));

        // The cold path used to dereference the missing object and surface a
        // NullReferenceException, while the warm path reported the domain
        // error. Both now fail the same way.
        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("io", error.Code);
        Assert.Contains("no artifact object", error.Message);
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

    [Fact]
    public void ExternalStagingIsRecognizedAndReclaimedUnderTheArtifactLock()
    {
        using var fixture = new Fixture();
        var sha = new string('a', 64);
        var directory = Path.Combine(
            fixture.StoreDirectory,
            sha + ".ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "artifact"), "partial");

        Assert.True(StoreStaging.IsStagingName(Path.GetFileName(directory), out var artifact));
        Assert.Equal(sha, artifact);
        Assert.Contains(directory, StoreStaging.Enumerate(fixture.StoreDirectory));

        using (ProcessLock.Acquire(
            Path.Combine(fixture.StoreDirectory, ".locks", sha + ".lock"),
            "test artifact"))
        {
            Assert.False(StoreStaging.TryDelete(fixture.StoreDirectory, directory));
            Assert.True(Directory.Exists(directory));
        }

        Assert.True(StoreStaging.TryDelete(fixture.StoreDirectory, directory));
        Assert.False(Directory.Exists(directory));
    }
}
