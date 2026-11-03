using System.Text.Json;
using Xunit;

namespace Seiko.Tests;

public sealed class AdoptRemoveTests
{
    [Fact]
    public void Remove_AdoptKeepsAFileThePackageNoLongerOwns()
    {
        using var fixture = Seed();
        var client = new SeikoClient();
        var deployed = Path.Combine(fixture.Root, "hello.txt");
        File.WriteAllText(deployed, "edited");

        client.Remove(fixture.Root, "hello", storeRoot: fixture.StoreDirectory, adopt: true);

        Assert.True(File.Exists(deployed));
        Assert.Equal("edited", File.ReadAllText(deployed));
        var lockFile = client.ReadLock(fixture.Root)!;
        Assert.Empty(lockFile.Sei);
        var state = JsonSerializer.Deserialize(
            File.ReadAllBytes(Path.Combine(fixture.Root, ".seiko", "state.json")),
            SeikoJsonContext.Default.LocalState)!;
        Assert.Empty(state.Files);
    }

    [Fact]
    public void Remove_ForceDeletesAnEditedFile()
    {
        using var fixture = Seed();
        var client = new SeikoClient();
        var deployed = Path.Combine(fixture.Root, "hello.txt");
        File.WriteAllText(deployed, "edited");

        client.Remove(fixture.Root, "hello", storeRoot: fixture.StoreDirectory, force: true);

        Assert.False(File.Exists(deployed));
    }

    [Fact]
    public void Remove_KeepsAnAlreadyAdoptedFileWithoutTheFlag()
    {
        using var fixture = Seed();
        var client = new SeikoClient();
        var deployed = Path.Combine(fixture.Root, "hello.txt");
        File.WriteAllText(deployed, "edited");
        client.Add(fixture.Root, "hello", storeRoot: fixture.StoreDirectory, adopt: true);
        Assert.Equal("edited", File.ReadAllText(deployed));

        client.Remove(fixture.Root, "hello", storeRoot: fixture.StoreDirectory);

        Assert.True(File.Exists(deployed));
        Assert.Equal("edited", File.ReadAllText(deployed));
    }

    [Fact]
    public void Remove_StillBlocksAnEditedFileWithoutFlags()
    {
        using var fixture = Seed();
        var client = new SeikoClient();
        File.WriteAllText(Path.Combine(fixture.Root, "hello.txt"), "edited");

        var error = Assert.Throws<SeikoException>(
            () => client.Remove(fixture.Root, "hello", storeRoot: fixture.StoreDirectory));

        Assert.Equal("deploy", error.Code);
        Assert.Single(ConfigLoader.Load(fixture.Root).Sei);
    }

    private static Fixture Seed()
    {
        var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        return fixture;
    }
}
