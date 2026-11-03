using Xunit;

namespace Seiko.Tests;

public sealed class OfflineTests
{
    [Fact]
    public void Sync_OfflineRequiresLockedOrFrozen()
    {
        using var fixture = new Fixture();
        Seed(fixture);

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory, offline: true));

        Assert.Equal("source", error.Code);
        Assert.Contains("--locked", error.Message);
    }

    [Fact]
    public void Sync_LockedOfflineReplaysWithoutTheServer()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        Seed(fixture, server.BaseUrl);
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var result = client.Sync(
            fixture.Root,
            fixture.StoreDirectory,
            mode: SyncMode.Locked,
            offline: true);

        Assert.Equal(1, result.SeiInstalled);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Sync_LockedOfflineUsesCachedHttpMetadata()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        Seed(fixture, server.BaseUrl);
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        Assert.NotEmpty(Directory.EnumerateFiles(
            Path.Combine(fixture.StoreDirectory, ".metadata")));

        var result = client.Sync(
            fixture.Root,
            fixture.StoreDirectory,
            mode: SyncMode.Locked,
            offline: true);

        Assert.Equal(1, result.SeiInstalled);
    }

    [Fact]
    public void Sync_LockedOfflineListsAllMissingMetadataAtOnce()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("a", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["a.txt"] = "a" } }],
        });
        fixture.AddSei("b", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["b.txt"] = "b" } }],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("a", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        Directory.Delete(Path.Combine(fixture.StoreDirectory, ".metadata"), recursive: true);

        var error = Assert.Throws<SeikoException>(() => client.Sync(
            fixture.Root,
            fixture.StoreDirectory,
            mode: SyncMode.Locked,
            offline: true));

        Assert.Equal("source", error.Code);
        Assert.Contains("index metadata", error.Message);
        Assert.Contains("sei/a.json", error.Message);
    }

    [Fact]
    public void Sync_LockedOfflineListsMissingArtifactsAtOnce()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        Seed(fixture, server.BaseUrl);
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var digest = client.ReadLock(fixture.Root)!.Sei.Single().Artifact.Sha256;
        Directory.Delete(Path.Combine(fixture.StoreDirectory, digest), recursive: true);

        var error = Assert.Throws<SeikoException>(() => client.Sync(
            fixture.Root,
            fixture.StoreDirectory,
            mode: SyncMode.Locked,
            offline: true));

        Assert.Equal("source", error.Code);
        Assert.Contains("missing cached artifacts", error.Message);
        Assert.Contains("hello 1.0.0", error.Message);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Sync_LockedOfflineFailsWhenMetadataIsNotCached()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        Seed(fixture, server.BaseUrl);
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        Directory.Delete(Path.Combine(fixture.StoreDirectory, ".metadata"), recursive: true);

        var error = Assert.Throws<SeikoException>(() => client.Sync(
            fixture.Root,
            fixture.StoreDirectory,
            mode: SyncMode.Locked,
            offline: true));

        Assert.Equal("source", error.Code);
        Assert.Contains("missing cached metadata", error.Message);
    }

    [Fact]
    public void Sync_FrozenOfflineReplays()
    {
        using var fixture = new Fixture();
        Seed(fixture);
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var result = client.Sync(
            fixture.Root,
            fixture.StoreDirectory,
            mode: SyncMode.Frozen,
            offline: true);

        Assert.Equal(1, result.SeiInstalled);
    }

    [Fact]
    public void Add_OfflineIsRejectedWithoutDryRun()
    {
        using var fixture = new Fixture();
        Seed(fixture);

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root,
            "hello",
            storeRoot: fixture.StoreDirectory,
            offline: true));

        Assert.Equal("source", error.Code);
        Assert.Single(ConfigLoader.Load(fixture.Root).Sei);
    }

    [Fact]
    public void Add_DryRunOfflineUsesTheCache()
    {
        using var fixture = new Fixture();
        Seed(fixture);
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var result = client.Add(
            fixture.Root,
            "hello",
            storeRoot: fixture.StoreDirectory,
            dryRun: true,
            offline: true);

        Assert.Contains(result.Preview!.Changes, change => change.Action == "keep");
        Assert.Equal("[1.0.0]", Assert.Single(ConfigLoader.Load(fixture.Root).Sei).Version);
        Assert.Equal("1.0.0", client.ReadLock(fixture.Root)!.Sei.Single().Version);
    }

    [Fact]
    public void Info_OfflineUsesOnlyTheCache()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        Seed(fixture, server.BaseUrl);
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var info = client.Info(
            fixture.Root,
            "hello",
            offline: true,
            storeRoot: fixture.StoreDirectory);

        Assert.Equal("hello", info.Name);
    }

    [Fact]
    public void Info_OfflineFailsWhenTheEntryIsNotCached()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        Seed(fixture, server.BaseUrl);
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        Directory.Delete(Path.Combine(fixture.StoreDirectory, ".metadata"), recursive: true);

        var error = Assert.Throws<SeikoException>(() => client.Info(
            fixture.Root,
            "hello",
            offline: true,
            storeRoot: fixture.StoreDirectory));

        Assert.Equal("source", error.Code);
        Assert.Contains("sei/hello.json", error.Message);
    }

    private static void Seed(Fixture fixture, string? httpBaseUrl = null)
    {
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        if (httpBaseUrl is null)
        {
            fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        }
        else
        {
            fixture.WriteHttpConfig(httpBaseUrl, "win-x64", new SpecFixture("hello", "[1.0.0]"));
        }
    }
}
