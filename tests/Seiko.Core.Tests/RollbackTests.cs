using System.Text.Json;
using Xunit;

namespace Seiko.Core.Tests;

public sealed class RollbackTests
{
    [Fact]
    public void Rollback_RestoresPreviousGeneration()
    {
        using var fixture = new Fixture();
        fixture.AddPackage(
            "hello",
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v1" } }] },
            new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v2" } }] });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var engine = new SeikoEngine();
        engine.Sync(fixture.Root, fixture.StoreDirectory);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));

        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));
        engine.Sync(fixture.Root, fixture.StoreDirectory);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));

        var result = engine.Rollback(fixture.Root, fixture.StoreDirectory);

        Assert.Equal(1, result.Generation);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
        Assert.Equal("1.0.0", engine.ReadLock(fixture.Root)!.Packages.Single().Version);
        var state = JsonSerializer.Deserialize(
            File.ReadAllBytes(Path.Combine(fixture.Root, ".seiko", "state.json")),
            SeikoJsonContext.Default.LocalState)!;
        Assert.Equal(1, state.Generation);
    }

    [Fact]
    public void Rollback_RequiresPreviousGeneration()
    {
        using var fixture = new Fixture();
        fixture.AddPackage("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v1" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var engine = new SeikoEngine();
        engine.Sync(fixture.Root, fixture.StoreDirectory);

        var error = Assert.Throws<SeikoException>(() => engine.Rollback(fixture.Root, fixture.StoreDirectory));
        Assert.Contains("previous generation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rollback_ToExplicitGeneration()
    {
        using var fixture = new Fixture();
        fixture.AddPackage(
            "hello",
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v1" } }] },
            new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v2" } }] },
            new VersionFixture { Version = "3.0.0", Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v3" } }] });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var engine = new SeikoEngine();
        engine.Sync(fixture.Root, fixture.StoreDirectory);
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));
        engine.Sync(fixture.Root, fixture.StoreDirectory);
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[3.0.0]"));
        engine.Sync(fixture.Root, fixture.StoreDirectory);

        var result = engine.Rollback(fixture.Root, fixture.StoreDirectory, toGeneration: 1);

        Assert.Equal(1, result.Generation);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
        Assert.Equal("1.0.0", engine.ReadLock(fixture.Root)!.Packages.Single().Version);
    }
}
