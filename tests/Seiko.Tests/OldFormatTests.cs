using Xunit;

namespace Seiko.Tests;

public sealed class OldFormatTests
{
    [Fact]
    public void Sync_RejectsAnOldLock()
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
        var lockPath = Path.Combine(fixture.Root, "seiko.lock.json");
        File.WriteAllText(
            lockPath,
            File.ReadAllText(lockPath).Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1"));

        var error = Assert.Throws<SeikoException>(
            () => client.Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("io", error.Code);
        Assert.Contains("unsupported lock schemaVersion 1", error.Message);
    }

    [Fact]
    public void Rollback_RejectsAnOldGenerationLock()
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
        var snapshot = Path.Combine(fixture.Root, ".seiko", "generations", "1", "lock.json");
        File.WriteAllText(
            snapshot,
            File.ReadAllText(snapshot).Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1"));

        var error = Assert.Throws<SeikoException>(
            () => client.Rollback(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("io", error.Code);
        Assert.Contains("unsupported lock schemaVersion 1", error.Message);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Sync_RejectsAnOldIndexSchema()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var index = Path.Combine(fixture.SourceDirectory, "index.json");
        var text = File.ReadAllText(index);
        File.WriteAllText(index, text.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1").Replace("\"schemaVersion\":2", "\"schemaVersion\":1"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("source", error.Code);
        Assert.Contains("unsupported index schemaVersion 1", error.Message);
    }

    [Fact]
    public void Config_RejectsAnOldSelectorsCarrier()
    {
        using var fixture = new Fixture();
        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            $$"""
            schemaVersion = {{SeikoConfig.CurrentSchemaVersion}}
            sources = []
            rid = "win-x64"
            selectors = { carrier = "x" }
            sei = []
            """);

        // selectors is unknown at the top level but must be tolerated, so the
        // real old shape is a component group.
        var error = Assert.Throws<SeikoException>(() => ConfigLoader.Load(fixture.Root));
        Assert.Equal("config", error.Code);
    }

    [Fact]
    public void Sync_RejectsAnOldLocalState()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var stateDirectory = Path.Combine(fixture.Root, ".seiko");
        Directory.CreateDirectory(Path.Combine(stateDirectory, "generations", "7"));
        File.WriteAllText(
            Path.Combine(stateDirectory, "state.json"),
            """{"schemaVersion":1,"generation":7,"files":{}}""");
        File.WriteAllText(Path.Combine(stateDirectory, "generations", "7", "lock.json"), "{}");

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("io", error.Code);
        Assert.Contains("unsupported deployment state schemaVersion 1", error.Message);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "hello.txt")));
        Assert.True(Directory.Exists(Path.Combine(stateDirectory, "generations", "7")));
    }

    [Fact]
    public void Rollback_RejectsAnOldLocalState()
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
        var statePath = Path.Combine(fixture.Root, ".seiko", "state.json");
        File.WriteAllText(
            statePath,
            File.ReadAllText(statePath).Replace(
                $"\"schemaVersion\": {LocalState.CurrentSchemaVersion}",
                "\"schemaVersion\": 1"));

        var error = Assert.Throws<SeikoException>(
            () => client.Rollback(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("io", error.Code);
        Assert.Contains("unsupported deployment state schemaVersion 1", error.Message);
    }

    [Fact]
    public void Sync_ContinuesAfterARollback()
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
        client.Rollback(fixture.Root, fixture.StoreDirectory);

        var result = client.Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal(1, result.SeiInstalled);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
        var state = System.Text.Json.JsonSerializer.Deserialize(
            File.ReadAllBytes(Path.Combine(fixture.Root, ".seiko", "state.json")),
            SeikoJsonContext.Default.LocalState)!;
        Assert.Equal(2, state.Generation);
    }
}
