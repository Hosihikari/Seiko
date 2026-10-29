using Xunit;

namespace Seiko.Tests;

public sealed class SyncTests
{
    [Fact]
    public void Sync_InstallsDependenciesAndProjectsPayload()
    {
        using var fixture = new Fixture();
        fixture.AddPackage("levilamina", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddPackage("hosihikari", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddPackage("hello-csharp", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("levilamina", null), new DepFixture("hosihikari", "[1.0.0,2.0.0)")],
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files =
                    {
                        ["plugins/hello-csharp/main.cs"] = "Console.WriteLine(\"hello\");",
                        ["plugins/hello-csharp/manifest.json"] = "{}",
                    },
                },
            ],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello-csharp", "[1.0.0,2.0.0)"));

        var engine = new SeikoClient();
        var result = engine.Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal(3, result.SeiInstalled);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "plugins", "hello-csharp", "main.cs")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "sei.json")));
        Assert.True(File.Exists(Path.Combine(fixture.Root, ".seiko", "installed", "hello-csharp", "1.0.0", "sei.json")));

        var lockFile = engine.ReadLock(fixture.Root)!;
        Assert.Equal(3, lockFile.Packages.Count);
        Assert.True(lockFile.Packages.Single(p => p.Name == "hello-csharp").Direct);
        Assert.False(lockFile.Packages.Single(p => p.Name == "levilamina").Direct);
    }

    [Fact]
    public void Sync_SelectsArtifactByCarrierAndRid()
    {
        using var fixture = new Fixture();
        fixture.AddPackage("levilamina", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddPackage("endstone", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddPackage("hosihikari-hosting", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Selectors = new Dictionary<string, string> { ["carrier"] = "levilamina", ["rid"] = "win-x64" },
                    Files = { ["selected.txt"] = "levilamina" },
                },
                new ArtifactFixture
                {
                    Selectors = new Dictionary<string, string> { ["carrier"] = "endstone", ["rid"] = "win-x64" },
                    Files = { ["selected.txt"] = "endstone" },
                },
            ],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("levilamina"), new SpecFixture("hosihikari-hosting"));

        var engine = new SeikoClient();
        var result = engine.Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("levilamina", File.ReadAllText(Path.Combine(fixture.Root, "selected.txt")));
        var hosting = result.Lock.Packages.Single(p => p.Name == "hosihikari-hosting");
        Assert.Equal("levilamina", hosting.Artifact.Selectors!["carrier"]);
    }

    [Fact]
    public void Sync_RejectsChecksumMismatch()
    {
        using var fixture = new Fixture();
        fixture.AddPackage("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        fixture.CorruptChecksum("hello");

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));
        Assert.Contains("checksum mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sync_RemovesStaleFilesOnNextSync()
    {
        using var fixture = new Fixture();
        fixture.AddPackage("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var engine = new SeikoClient();
        engine.Sync(fixture.Root, fixture.StoreDirectory);
        var file = Path.Combine(fixture.Root, "hello.txt");
        Assert.True(File.Exists(file));

        fixture.WriteConfig("win-x64");
        engine.Sync(fixture.Root, fixture.StoreDirectory);

        Assert.False(File.Exists(file));
        Assert.Empty(engine.ReadLock(fixture.Root)!.Packages);
    }

    [Fact]
    public void Sync_WritesDeterministicLock()
    {
        using var fixture = new Fixture();
        fixture.AddPackage("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var engine = new SeikoClient();
        engine.Sync(fixture.Root, fixture.StoreDirectory);
        var lockPath = Path.Combine(fixture.Root, "seiko.lock.json");
        var first = File.ReadAllText(lockPath);

        engine.Sync(fixture.Root, fixture.StoreDirectory);
        var second = File.ReadAllText(lockPath);

        Assert.Equal(first, second);
    }
}
