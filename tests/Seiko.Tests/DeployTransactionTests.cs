using System.Text.Json;
using Xunit;

namespace Seiko.Tests;

public sealed class DeployTransactionTests
{
    [Fact]
    public void Sync_RollsBackPartialDeployment()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture
            {
                Files =
                {
                    ["a.txt"] = "one-a",
                    ["b.txt"] = "one-b",
                },
            }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture
            {
                Files =
                {
                    ["a.txt"] = "two-a",
                    ["b.txt"] = "two-b",
                },
            }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));

        try
        {
            Deployer.AppliedForTests.Value = count =>
            {
                if (count == 1)
                {
                    throw new IOException("injected deployment failure");
                }
            };
            Assert.Throws<IOException>(
                () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));
        }
        finally
        {
            Deployer.AppliedForTests.Value = null;
        }

        Assert.Equal("one-a", File.ReadAllText(Path.Combine(fixture.Root, "a.txt")));
        Assert.Equal("one-b", File.ReadAllText(Path.Combine(fixture.Root, "b.txt")));
        Assert.Equal("1.0.0", new SeikoClient().ReadLock(fixture.Root)!.Sei.Single().Version);
        var state = JsonSerializer.Deserialize(
            File.ReadAllBytes(Path.Combine(fixture.Root, ".seiko", "state.json")),
            SeikoJsonContext.Default.LocalState);
        Assert.Equal(1, state!.Generation);
        Assert.False(File.Exists(Path.Combine(fixture.Root, ".seiko", "transaction.json")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".seiko", "transaction")));
    }

    [Fact]
    public void Recover_RollsBackAnUncommittedJournal()
    {
        using var fixture = new Fixture();
        var stateDirectory = Path.Combine(fixture.Root, ".seiko");
        var file = Path.Combine(fixture.Root, "file.txt");
        File.WriteAllText(file, "old");

        var transaction = DeploymentTransaction.Begin(fixture.Root, stateDirectory);
        transaction.WillChange("file.txt");
        transaction.WriteJournal();
        File.WriteAllText(file, "new");

        DeploymentTransaction.Recover(fixture.Root, stateDirectory);

        Assert.Equal("old", File.ReadAllText(file));
        Assert.False(File.Exists(Path.Combine(stateDirectory, "transaction.json")));
        Assert.False(Directory.Exists(Path.Combine(stateDirectory, "transaction")));
    }
}
