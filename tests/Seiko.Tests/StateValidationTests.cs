using System.Text.Json.Nodes;
using Xunit;

namespace Seiko.Tests;

public sealed class StateValidationTests
{
    [Fact]
    public void Sync_ReportsANullFileRecordInsteadOfCrashing()
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
        var node = JsonNode.Parse(File.ReadAllText(statePath))!;
        node["files"]!["hello.txt"] = null;
        File.WriteAllText(statePath, node.ToJsonString());

        var error = Assert.Throws<SeikoException>(
            () => client.Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("deploy", error.Code);
        Assert.Contains("no record", error.Message);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Frozen_ReportsANullLockRelationInsteadOfCrashing()
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
        var node = JsonNode.Parse(File.ReadAllText(lockPath))!;
        node["sei"]![0]!["dependencies"] = new JsonArray((JsonNode?)null);
        File.WriteAllText(lockPath, node.ToJsonString());

        var error = Assert.Throws<SeikoException>(
            () => client.Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen));

        Assert.Equal("lock", error.Code);
        Assert.Contains("invalid lock file", error.Message);
    }
}
