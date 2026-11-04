using System.Text.Json.Nodes;
using Xunit;

namespace Seiko.Tests;

public sealed class R2ProbeTests
{
    [Fact]
    public void F2_CorruptAdoptedSnapshotIsRejectedBeforeTouchingTheEnvironment()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "hello",
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v1" } }] },
            new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v2" } }] });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var deployed = Path.Combine(fixture.Root, "hello.txt");
        File.WriteAllText(deployed, "ADOPTED");
        client.Sync(fixture.Root, fixture.StoreDirectory, adopt: true);
        client.Sync(fixture.Root, fixture.StoreDirectory, force: true);
        var lockBefore = File.ReadAllBytes(Path.Combine(fixture.Root, "seiko.lock.json"));
        File.WriteAllText(
            Path.Combine(fixture.Root, ".seiko", "generations", "2", "adopted", "hello.txt"),
            "CORRUPTED SNAPSHOT");

        var error = Assert.Throws<SeikoException>(
            () => client.Rollback(fixture.Root, fixture.StoreDirectory, toGeneration: 2));

        Assert.Equal("checksum", error.Code);
        Assert.Contains("corrupted", error.Message);
        Assert.Equal("v1", File.ReadAllText(deployed));
        Assert.Equal(lockBefore, File.ReadAllBytes(Path.Combine(fixture.Root, "seiko.lock.json")));
    }

    [Fact]
    public void F2_AMissingGenerationStateIsRejected()
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
        var deployed = Path.Combine(fixture.Root, "hello.txt");
        File.WriteAllText(deployed, "ADOPTED");
        client.Sync(fixture.Root, fixture.StoreDirectory, adopt: true);
        client.Sync(fixture.Root, fixture.StoreDirectory, force: true);
        File.Delete(Path.Combine(fixture.Root, ".seiko", "generations", "2", "state.json"));

        var error = Assert.Throws<SeikoException>(
            () => client.Rollback(fixture.Root, fixture.StoreDirectory, toGeneration: 2));

        Assert.Equal("io", error.Code);
        Assert.Contains("no state", error.Message);
        Assert.Equal("v1", File.ReadAllText(deployed));
    }

    [Fact]
    public void F3_UnsolvableArtifactCountsAsAmbiguous()
    {
        using var fixture = new Fixture();
        fixture.AddSei("marker", new VersionFixture
        {
            Version = "1.0.0",
            Conflicts = [new DepFixture("aaa", "*")],
            Artifacts = [new ArtifactFixture { Files = { ["m.txt"] = "m" } }],
        });
        fixture.AddSei("aaa", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture { Files = { ["a.txt"] = "plain" } },
                new ArtifactFixture
                {
                    Dependencies = [new DepFixture("marker", "*")],
                    Files = { ["a.txt"] = "with" },
                },
            ],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("aaa", "[1.0.0]"));

        // The artifact that pulls in marker conflicts with marker's own
        // conflict on aaa, so only the plain artifact is actually solvable.
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("plain", File.ReadAllText(Path.Combine(fixture.Root, "a.txt")));
        var lockFile = new SeikoClient().ReadLock(fixture.Root)!;
        Assert.DoesNotContain(lockFile.Sei, sei => sei.Name == "marker");
    }

    [Fact]
    public void F4_LockedOfflineIgnoresUnselectedArtifactRelations()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("aaa", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture { Rid = "osx-arm64", Files = { ["a.txt"] = "osx" } },
                new ArtifactFixture
                {
                    Rid = "win-x64",
                    Dependencies = [new DepFixture("unneeded", "*")],
                    Files = { ["a.txt"] = "win" },
                },
            ],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "osx-arm64", new SpecFixture("aaa", "[1.0.0]"));
        // A winning non-retaining relation whose target is not locked.
        fixture.AddSei("bbb", new VersionFixture
        {
            Version = "1.0.0",
            OptionalPeers = [new DepFixture("also-unneeded", "*")],
            Artifacts = [new ArtifactFixture { Files = { ["b.txt"] = "b" } }],
        });
        fixture.WriteHttpConfig(
            server.BaseUrl,
            "osx-arm64",
            new SpecFixture("aaa", "[1.0.0]"),
            new SpecFixture("bbb", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var result = client.Sync(
            fixture.Root,
            fixture.StoreDirectory,
            mode: SyncMode.Locked,
            offline: true);

        Assert.Equal(2, result.SeiInstalled);
        Assert.Equal("osx", File.ReadAllText(Path.Combine(fixture.Root, "a.txt")));
    }

    [Fact]
    public void F5_ANewer404ReplacesAStaleSuccess()
    {
        using var first = new Fixture();
        using var firstServer = new TestHttpServer(first.SourceDirectory);
        first.AddSei("aaa", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["a.txt"] = "one" } }],
        });
        using var second = new Fixture();
        using var secondServer = new TestHttpServer(second.SourceDirectory);
        second.AddSei("aaa", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["a.txt"] = "two" } }],
        });
        first.WriteConfig("win-x64", new SpecFixture("aaa", "*"));
        second.WriteConfig("win-x64", new SpecFixture("aaa", "*"));
        File.WriteAllText(
            Path.Combine(first.Root, "seiko.conf"),
            $$"""
            schemaVersion = {{SeikoConfig.CurrentSchemaVersion}}
            sources = [
              { id = "first", index = "{{firstServer.BaseUrl}}index.json" },
              { id = "second", index = "{{secondServer.BaseUrl}}index.json" }
            ]
            rid = "win-x64"
            sei = [ { name = "aaa", version = "*" } ]
            """);
        var client = new SeikoClient();
        client.Sync(first.Root, first.StoreDirectory);
        Assert.Equal("1.0.0", client.ReadLock(first.Root)!.Sei.Single().Version);

        File.Delete(Path.Combine(first.SourceDirectory, "sei", "aaa.json"));
        client.Sync(first.Root, first.StoreDirectory);
        Assert.Equal("2.0.0", client.ReadLock(first.Root)!.Sei.Single().Version);

        // Offline must read the first source as absent, not as its stale
        // 1.0.0 answer, or the locked replay would see an out-of-date lock.
        var info = client.Info(
            first.Root,
            "aaa",
            offline: true,
            storeRoot: first.StoreDirectory);

        Assert.Equal("second", info.Source);
        Assert.Equal("2.0.0", Assert.Single(info.Versions).Version);
    }
}
