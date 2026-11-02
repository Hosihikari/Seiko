using System.Text.Json;
using Xunit;

namespace Seiko.Tests;

public sealed class ManagedFileTests
{
    [Fact]
    public void Sync_BlocksOverwritingAModifiedManagedFile()
    {
        using var fixture = Setup();
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var deployed = Path.Combine(fixture.Root, "hello.txt");
        File.WriteAllText(deployed, "edited");
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "two" } }],
        });

        var error = Assert.Throws<SeikoException>(
            () => client.Sync(fixture.Root, fixture.StoreDirectory, updateNames: new HashSet<string> { "hello" }));

        Assert.Equal("deploy", error.Code);
        Assert.Contains("--force", error.Message);
        Assert.Equal("edited", File.ReadAllText(deployed));
        Assert.Equal("1.0.0", client.ReadLock(fixture.Root)!.Sei.Single().Version);
    }

    [Fact]
    public void Sync_ForceOverwritesAModifiedManagedFile()
    {
        using var fixture = Setup();
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var deployed = Path.Combine(fixture.Root, "hello.txt");
        File.WriteAllText(deployed, "edited");
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "two" } }],
        });

        client.Sync(
            fixture.Root,
            fixture.StoreDirectory,
            updateNames: new HashSet<string> { "hello" },
            force: true);

        Assert.Equal("two", File.ReadAllText(deployed));
    }

    [Fact]
    public void Sync_AdoptKeepsTheEditedFileAndRecordsTheEdit()
    {
        using var fixture = Setup();
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var deployed = Path.Combine(fixture.Root, "hello.txt");
        File.WriteAllText(deployed, "edited");

        client.Sync(
            fixture.Root,
            fixture.StoreDirectory,
            updateNames: new HashSet<string> { "hello" },
            adopt: true);

        Assert.Equal("edited", File.ReadAllText(deployed));
        var state = JsonSerializer.Deserialize(
            File.ReadAllBytes(Path.Combine(fixture.Root, ".seiko", "state.json")),
            SeikoJsonContext.Default.LocalState)!;
        var entry = state.Files["hello.txt"];
        Assert.True(entry.Adopted);
        Assert.Equal(Hashing.Sha256Bytes("edited"u8), entry.Sha256);

        // A later sync keeps the adopted content without asking again.
        client.Sync(fixture.Root, fixture.StoreDirectory);
        Assert.Equal("edited", File.ReadAllText(deployed));
    }

    [Fact]
    public void Sync_AdoptKeepsAnUnmanagedFile()
    {
        using var fixture = Setup();
        File.WriteAllText(Path.Combine(fixture.Root, "hello.txt"), "mine");

        new SeikoClient().Sync(
            fixture.Root,
            fixture.StoreDirectory,
            adopt: true);

        Assert.Equal("mine", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Sync_ForceOverwritesAnUnmanagedFile()
    {
        using var fixture = Setup();
        File.WriteAllText(Path.Combine(fixture.Root, "hello.txt"), "mine");

        new SeikoClient().Sync(
            fixture.Root,
            fixture.StoreDirectory,
            force: true);

        Assert.Equal("hello", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Sync_BlocksAnUnmanagedFileByDefault()
    {
        using var fixture = Setup();
        File.WriteAllText(Path.Combine(fixture.Root, "hello.txt"), "mine");

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("deploy", error.Code);
        Assert.Contains("--adopt", error.Message);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    private static Fixture Setup()
    {
        var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "*"));
        return fixture;
    }
}
