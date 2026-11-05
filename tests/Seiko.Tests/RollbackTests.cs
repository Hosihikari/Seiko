using System.Text.Json;
using Xunit;

namespace Seiko.Tests;

public sealed class RollbackTests
{
    [Fact]
    public void Rollback_RestoresPreviousGeneration()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "hello",
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v1" } }] },
            new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v2" } }] });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));

        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));
        client.Sync(fixture.Root, fixture.StoreDirectory);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));

        var result = client.Rollback(fixture.Root, fixture.StoreDirectory);

        Assert.Equal(1, result.Generation);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
        Assert.Equal("1.0.0", client.ReadLock(fixture.Root)!.Sei.Single().Version);
        Assert.Equal("[1.0.0]", Assert.Single(ConfigLoader.Load(fixture.Root).Sei).Version);
        var state = JsonSerializer.Deserialize(
            File.ReadAllBytes(Path.Combine(fixture.Root, ".seiko", "state.json")),
            SeikoJsonContext.Default.LocalState)!;
        Assert.Equal(1, state.Generation);
    }

    [Fact]
    public void Sync_RecordsConfigInGenerationSnapshot()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v1" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        var config = Path.Combine(fixture.Root, "seiko.conf");
        var snapshot = Path.Combine(fixture.Root, ".seiko", "generations", "1", "seiko.conf");
        Assert.Equal(File.ReadAllBytes(config), File.ReadAllBytes(snapshot));
    }

    [Fact]
    public void Rollback_RestoresConfigWhenDeploymentFails()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "hello",
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v1" } }] },
            new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v2" } }] });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var configBefore = File.ReadAllBytes(Path.Combine(fixture.Root, "seiko.conf"));

        try
        {
            Deployer.AppliedForTests.Value = count =>
            {
                if (count == 1)
                {
                    throw new IOException("injected rollback deployment failure");
                }
            };
            Assert.Throws<IOException>(
                () => client.Rollback(fixture.Root, fixture.StoreDirectory));
        }
        finally
        {
            Deployer.AppliedForTests.Value = null;
        }

        Assert.Equal(configBefore, File.ReadAllBytes(Path.Combine(fixture.Root, "seiko.conf")));
        Assert.Equal("[2.0.0]", Assert.Single(ConfigLoader.Load(fixture.Root).Sei).Version);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
        Assert.Equal("2.0.0", client.ReadLock(fixture.Root)!.Sei.Single().Version);
        Assert.False(File.Exists(Path.Combine(fixture.Root, ".seiko", "transaction.json")));
    }

    [Fact]
    public void Rollback_RejectsGenerationWithoutConfig()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "hello",
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v1" } }] },
            new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v2" } }] });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));
        client.Sync(fixture.Root, fixture.StoreDirectory);
        File.Delete(Path.Combine(fixture.Root, ".seiko", "generations", "1", "seiko.conf"));

        var error = Assert.Throws<SeikoException>(
            () => client.Rollback(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("io", error.Code);
        Assert.Contains("no config", error.Message);
        Assert.Equal("[2.0.0]", Assert.Single(ConfigLoader.Load(fixture.Root).Sei).Version);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Rollback_RedownloadsMissingStoreArtifact()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "hello",
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v1" } }] },
            new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v2" } }] });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var snapshot = LockFile.Read(
            Path.Combine(fixture.Root, ".seiko", "generations", "1", "lock.json"))!;
        var sha = snapshot.Sei.Single().Artifact.Sha256;
        Directory.Delete(Path.Combine(fixture.StoreDirectory, sha), recursive: true);
        File.Delete(Path.Combine(fixture.StoreDirectory, ".meta", sha + ".json"));

        var result = client.Rollback(fixture.Root, fixture.StoreDirectory);

        Assert.Equal(1, result.Generation);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
        Assert.True(Directory.Exists(Path.Combine(fixture.StoreDirectory, sha)));
    }

    [Fact]
    public void PreviewRollback_ReportsMissingStoreArtifact()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "hello",
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v1" } }] },
            new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v2" } }] });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var snapshot = LockFile.Read(
            Path.Combine(fixture.Root, ".seiko", "generations", "1", "lock.json"))!;
        var sha = snapshot.Sei.Single().Artifact.Sha256;
        Directory.Delete(Path.Combine(fixture.StoreDirectory, sha), recursive: true);
        File.Delete(Path.Combine(fixture.StoreDirectory, ".meta", sha + ".json"));

        var preview = client.PreviewRollback(fixture.Root, storeRoot: fixture.StoreDirectory);

        Assert.Equal(1, preview.ToGeneration);
        Assert.Contains("hello.txt", preview.MissingStore);
    }

    [Fact]
    public void Rollback_RequiresPreviousGeneration()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v1" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var error = Assert.Throws<SeikoException>(() => client.Rollback(fixture.Root, fixture.StoreDirectory));
        Assert.Contains("previous generation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rollback_ToExplicitGeneration()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "hello",
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v1" } }] },
            new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v2" } }] },
            new VersionFixture { Version = "3.0.0", Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v3" } }] });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));
        client.Sync(fixture.Root, fixture.StoreDirectory);
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[3.0.0]"));
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var result = client.Rollback(fixture.Root, fixture.StoreDirectory, toGeneration: 1);

        Assert.Equal(1, result.Generation);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
        Assert.Equal("1.0.0", client.ReadLock(fixture.Root)!.Sei.Single().Version);
    }
}
