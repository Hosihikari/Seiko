using Xunit;

namespace Seiko.Tests;

public sealed class RemoveTests
{
    [Fact]
    public void Remove_BlocksSilentArtifactSwitch()
    {
        using var fixture = new Fixture();
        fixture.AddSei("marker", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["marker.txt"] = "marker" } }],
        });
        fixture.AddSei("host", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture { Files = { ["host.txt"] = "plain" } },
                new ArtifactFixture
                {
                    Prerequisites = [new DepFixture("marker", "*")],
                    Files = { ["host.txt"] = "preferred" },
                },
            ],
        });
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("host", "[1.0.0]"),
            new SpecFixture("marker", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var configPath = Path.Combine(fixture.Root, "seiko.conf");
        var before = File.ReadAllText(configPath);
        var error = Assert.Throws<SeikoException>(
            () => client.Remove(fixture.Root, "marker", storeRoot: fixture.StoreDirectory));

        Assert.Equal("resolve", error.Code);
        Assert.Contains("would change host", error.Message);
        Assert.Equal(before, File.ReadAllText(configPath));
    }

    [Fact]
    public void AddMany_AddsAndRemovesInOneOperation()
    {
        using var fixture = new Fixture();
        fixture.AddSei("old", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["old.txt"] = "old" } }],
        });
        fixture.AddSei("new", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["new.txt"] = "new" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("old", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        client.AddMany(
            fixture.Root,
            ["new@[1.0.0]"],
            ["old"],
            storeRoot: fixture.StoreDirectory);

        var config = ConfigLoader.Load(fixture.Root);
        Assert.Equal("new", Assert.Single(config.Sei).Name);
        var lockFile = client.ReadLock(fixture.Root)!;
        Assert.Contains(lockFile.Sei, sei => sei.Name == "new");
        Assert.DoesNotContain(lockFile.Sei, sei => sei.Name == "old");
    }

    [Fact]
    public void AddMany_BlocksSilentArtifactSwitch()
    {
        using var fixture = new Fixture();
        fixture.AddSei("marker", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["marker.txt"] = "marker" } }],
        });
        fixture.AddSei("host", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture { Files = { ["host.txt"] = "plain" } },
                new ArtifactFixture
                {
                    Prerequisites = [new DepFixture("marker", "*")],
                    Files = { ["host.txt"] = "preferred" },
                },
            ],
        });
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("host", "[1.0.0]"),
            new SpecFixture("marker", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var error = Assert.Throws<SeikoException>(
            () => client.AddMany(
                fixture.Root,
                [],
                ["marker"],
                storeRoot: fixture.StoreDirectory));

        Assert.Equal("resolve", error.Code);
        Assert.Contains("would change host", error.Message);
    }
}
