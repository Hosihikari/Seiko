using Xunit;

namespace Seiko.Tests;

public sealed class WorkflowTests
{
    [Fact]
    public void Why_ReturnsDependencyChain()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.AddSei("app", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("hello", "*")],
            Artifacts = [new ArtifactFixture { Files = { ["app.txt"] = "app" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("app", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        var chains = new SeikoClient().Why(fixture.Root, "hello");

        var chain = Assert.Single(chains);
        Assert.Equal(["app", "hello"], chain);
    }

    [Fact]
    public void Outdated_ReportsLatestInRangeAndOverall()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "one" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "two" } }],
        });

        var config = ConfigLoader.Load(fixture.Root);
        Assert.Equal("[1.0.0]", Assert.Single(config.Sei).Version);
        var item = Assert.Single(new SeikoClient().Outdated(fixture.Root));

        Assert.Equal("1.0.0", item.Current);
        Assert.Equal("1.0.0", item.LatestInRange);
        Assert.Equal("2.0.0", item.Latest);
    }

    [Fact]
    public void Cache_StatusVerifyAndClean()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        var client = new SeikoClient();

        Assert.Equal(1, client.CacheStatus(fixture.StoreDirectory).Artifacts);
        Assert.Empty(client.CacheVerify(fixture.StoreDirectory));

        var stale = Path.Combine(fixture.StoreDirectory, new string('a', 64));
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "junk"), "junk");
        Assert.Single(client.CacheVerify(fixture.StoreDirectory));

        var result = client.CacheClean(fixture.Root, fixture.StoreDirectory);

        Assert.Equal(1, result.Removed);
        Assert.False(Directory.Exists(stale));
        Assert.Equal(1, client.CacheStatus(fixture.StoreDirectory).Artifacts);
    }

    [Fact]
    public void Update_WithoutLatestKeepsRangeAndUpgrades()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "one" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0,3.0.0)"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "two" } }],
        });

        new SeikoClient().Update(fixture.Root, "hello", storeRoot: fixture.StoreDirectory);

        Assert.Equal("two", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
        Assert.Contains("[1.0.0,3.0.0)", File.ReadAllText(Path.Combine(fixture.Root, "seiko.conf")));
    }
}
