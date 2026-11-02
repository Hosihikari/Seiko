using Xunit;

namespace Seiko.Tests;

public sealed class PurgeTests
{
    [Fact]
    public void Remove_KeepsConfigAndDataByDefault()
    {
        using var fixture = new Fixture();
        Seed(fixture);

        new SeikoClient().Remove(fixture.Root, "hello", storeRoot: fixture.StoreDirectory);

        Assert.True(File.Exists(ConfigFile(fixture)));
        Assert.True(File.Exists(DataFile(fixture)));
    }

    [Fact]
    public void Remove_PurgeDeletesConfigAndData()
    {
        using var fixture = new Fixture();
        Seed(fixture);

        new SeikoClient().Remove(
            fixture.Root,
            "hello",
            storeRoot: fixture.StoreDirectory,
            purge: true);

        Assert.False(File.Exists(ConfigFile(fixture)));
        Assert.False(File.Exists(DataFile(fixture)));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".seiko", "transaction")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, ".seiko", "transaction.json")));
    }

    [Fact]
    public void Remove_PurgeRestoresConfigAndDataOnFailure()
    {
        using var fixture = new Fixture();
        Seed(fixture);
        fixture.CorruptChecksum("other");

        Assert.Throws<SeikoException>(() => new SeikoClient().Remove(
            fixture.Root,
            "hello",
            storeRoot: fixture.StoreDirectory,
            purge: true));

        Assert.Equal("kept", File.ReadAllText(ConfigFile(fixture)));
        Assert.Equal("data", File.ReadAllText(DataFile(fixture)));
        Assert.Equal(2, ConfigLoader.Load(fixture.Root).Sei.Count);
    }

    [Fact]
    public void Remove_PurgeWithoutFilesStillSucceeds()
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

        client.Remove(fixture.Root, "hello", storeRoot: fixture.StoreDirectory, purge: true);

        Assert.Empty(ConfigLoader.Load(fixture.Root).Sei);
    }

    private static void Seed(Fixture fixture)
    {
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.AddSei("other", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["other.txt"] = "other" } }],
        });
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("hello", "[1.0.0]"),
            new SpecFixture("other", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile(fixture))!);
        File.WriteAllText(ConfigFile(fixture), "kept");
        Directory.CreateDirectory(Path.GetDirectoryName(DataFile(fixture))!);
        File.WriteAllText(DataFile(fixture), "data");
    }

    private static string ConfigFile(Fixture fixture)
        => Path.Combine(fixture.Root, "config", "hosihikari", "hello.conf");

    private static string DataFile(Fixture fixture)
        => Path.Combine(fixture.Root, "data", "hosihikari", "hello", "state.txt");
}
