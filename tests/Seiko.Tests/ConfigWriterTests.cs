using Xunit;

namespace Seiko.Tests;

public sealed class ConfigWriterTests
{
    [Fact]
    public void WriteSpecs_ReplacesOnlyTheTargetSection()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var path = Path.Combine(fixture.Root, "seiko.conf");
        File.WriteAllText(path, """
# keep me
sources = [ { id = "local", index = "source/index.json" } ]
rid = "win-x64"
sei = [
  { name = "old", version = "1.0.0" },
]
components = []
""");

        ConfigWriter.WriteSpecs(fixture.Root, "sei", [new SeiSpec { Name = "hello", Version = "[1.0.0]" }]);

        var text = File.ReadAllText(path);
        Assert.Contains("# keep me", text);
        Assert.Contains("rid = \"win-x64\"", text);
        Assert.Contains("name = \"hello\"", text);
        Assert.DoesNotContain("name = \"old\"", text);
        Assert.Contains("components = []", text);
    }

    [Fact]
    public void SourceAndConfigCommands_RoundTrip()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var client = new SeikoClient();

        client.SourceAdd(
            fixture.Root,
            "mirror",
            "https://example.invalid/index.json",
            tokenHosts: ["cdn.example.invalid"]);
        var mirror = Assert.Single(client.ListSources(fixture.Root), source => source.Id == "mirror");
        Assert.Equal(["cdn.example.invalid"], mirror.TokenHosts);
        Assert.Equal("win-x64", client.ConfigGet(fixture.Root, "rid"));

        client.ConfigSet(fixture.Root, "rid", "linux-x64");
        Assert.Equal("linux-x64", client.ConfigGet(fixture.Root, "rid"));

        var sources = client.SourceRemove(fixture.Root, "mirror");
        Assert.DoesNotContain(sources, source => source.Id == "mirror");
        Assert.Contains("linux-x64", File.ReadAllText(Path.Combine(fixture.Root, "seiko.conf")));
    }

    [Fact]
    public void AddRemoveAndUpdate_ManageDirectDeclarations()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "one" } }],
        });
        fixture.WriteConfig("win-x64");
        var client = new SeikoClient();

        client.Add(fixture.Root, "hello@[1.0.0]", storeRoot: fixture.StoreDirectory);
        Assert.Equal("one", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
        Assert.Contains("version = \"[1.0.0]\"", File.ReadAllText(Path.Combine(fixture.Root, "seiko.conf")));

        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "two" } }],
        });
        client.Update(fixture.Root, "hello", latest: true, storeRoot: fixture.StoreDirectory);
        Assert.Equal("two", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
        Assert.Contains("version = \"[2.0.0]\"", File.ReadAllText(Path.Combine(fixture.Root, "seiko.conf")));

        client.Remove(fixture.Root, "hello", storeRoot: fixture.StoreDirectory);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "hello.txt")));
        Assert.Empty(client.ReadLock(fixture.Root)!.Sei);
    }
}
