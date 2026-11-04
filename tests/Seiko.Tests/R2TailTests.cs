using System.Text.Json.Nodes;
using Xunit;

namespace Seiko.Tests;

public sealed class R2TailTests
{
    [Fact]
    public void Sync_ReportsANullFileListInsteadOfCrashing()
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
        node["files"] = null;
        File.WriteAllText(statePath, node.ToJsonString());

        var error = Assert.Throws<SeikoException>(
            () => client.Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("deploy", error.Code);
        Assert.Contains("no file list", error.Message);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Transaction_RecoveryRemovesAStaleAdoptedStagingDirectory()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var deployed = Path.Combine(fixture.Root, "hello.txt");
        File.WriteAllText(deployed, "ADOPTED");
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory, adopt: true);

        // Simulate a crash after copying into staging but before the move: the
        // journal is uncommitted and the staging directory still exists.
        var generation = Path.Combine(fixture.Root, ".seiko", "generations", "1");
        const string suffix = "0123456789abcdef0123456789abcdef";
        var staging = Path.Combine(
            generation,
            ".adopted-staging-" + suffix);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "hello.txt"), "ADOPTED");
        var journal = JsonNode.Parse(
            $$"""
            {
              "schemaVersion": 1,
              "committed": false,
              "temporaryPaths": [ "{{".seiko/generations/1/.adopted-staging-" + suffix}}" ],
              "entries": []
            }
            """)!;
        File.WriteAllText(
            Path.Combine(fixture.Root, ".seiko", "transaction.json"),
            journal.ToJsonString());
        var markerName = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(
                        "temporary\n.seiko/generations/1/.adopted-staging-" + suffix)))
            .ToLowerInvariant() + ".created";
        var ownership = Path.Combine(fixture.Root, ".seiko", "transaction.created");
        Directory.CreateDirectory(ownership);
        File.WriteAllText(
            Path.Combine(ownership, markerName),
            "temporary\n.seiko/generations/1/.adopted-staging-" + suffix + "\n\n");

        client.Sync(fixture.Root, fixture.StoreDirectory);

        Assert.False(Directory.Exists(staging));
        Assert.True(Directory.Exists(Path.Combine(generation, "adopted")));
    }
}
