using Xunit;

namespace Seiko.Tests;

public sealed class AddTests
{
    [Fact]
    public void Add_WritesMajorRangeForStableVersion()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.2.3",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64");

        new SeikoClient().Add(fixture.Root, "hello", storeRoot: fixture.StoreDirectory);

        Assert.Equal("[1.2.3,2.0.0)", Assert.Single(ConfigLoader.Load(fixture.Root).Sei).Version);
    }

    [Fact]
    public void Add_WritesMinorRangeForZeroMajorVersion()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "0.2.3",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64");

        new SeikoClient().Add(fixture.Root, "hello", storeRoot: fixture.StoreDirectory);

        Assert.Equal("[0.2.3,0.3.0)", Assert.Single(ConfigLoader.Load(fixture.Root).Sei).Version);
    }

    [Fact]
    public void Add_RestoresConfigWhenSyncFails()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.2.3",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.CorruptChecksum("hello");
        fixture.WriteConfig("win-x64");

        Assert.Throws<SeikoException>(
            () => new SeikoClient().Add(fixture.Root, "hello", storeRoot: fixture.StoreDirectory));

        Assert.Empty(ConfigLoader.Load(fixture.Root).Sei);
    }

    [Fact]
    public void Add_WithoutRangePreservesExistingRange()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.2.3",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0,2.0.0)"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        client.Add(fixture.Root, "hello", storeRoot: fixture.StoreDirectory);

        Assert.Equal("[1.0.0,2.0.0)", Assert.Single(ConfigLoader.Load(fixture.Root).Sei).Version);
    }
}
