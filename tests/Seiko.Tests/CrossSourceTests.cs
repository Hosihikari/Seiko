using Xunit;

namespace Seiko.Tests;

public sealed class CrossSourceTests
{
    [Fact]
    public void Sync_RejectsDifferentDigestsForEquivalentArtifacts()
    {
        using var first = new Fixture();
        first.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "first" } }],
        });
        using var second = new Fixture();
        second.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "second" } }],
        });
        WriteConfig(first, second);

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(first.Root, first.StoreDirectory));

        Assert.Equal("checksum", error.Code);
        Assert.Contains("differs between", error.Message);
    }

    [Fact]
    public void Sync_AllowsDifferentDigestsForDifferentConditions()
    {
        using var first = new Fixture();
        first.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "first" } }],
        });
        using var second = new Fixture();
        second.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Rid = "linux-x64",
                    Files = { ["hello.txt"] = "second" },
                },
            ],
        });
        WriteConfig(first, second);

        new SeikoClient().Sync(first.Root, first.StoreDirectory);

        Assert.Equal("first", File.ReadAllText(Path.Combine(first.Root, "hello.txt")));
    }

    [Fact]
    public void Sync_ValidatesEveryMirroredIndexObject()
    {
        using var first = new Fixture();
        first.AddSei("a", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["a.txt"] = "a" } }],
        });
        first.AddSei("b", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["b.txt"] = "b" } }],
        });
        using var second = new Fixture();
        second.AddSei("a", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["a.txt"] = "a" } }],
        });
        second.AddSei("b", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["b.txt"] = "b" } }],
        });
        first.WriteConfig("win-x64", new SpecFixture("a", "[1.0.0]"), new SpecFixture("b", "[1.0.0]"));
        second.WriteConfig("win-x64");
        var firstIndex = Path.Combine(first.SourceDirectory, "index.json").Replace('\\', '/');
        var secondIndex = Path.Combine(second.SourceDirectory, "index.json").Replace('\\', '/');
        File.WriteAllText(
            Path.Combine(first.Root, "seiko.conf"),
            $$"""
            schemaVersion = {{SeikoConfig.CurrentSchemaVersion}}
            sources = [
              { id = "first", index = "{{firstIndex}}" },
              { id = "second", index = "{{secondIndex}}" }
            ]
            rid = "win-x64"
            sei = [ { name = "a", version = "[1.0.0]" }, { name = "b", version = "[1.0.0]" } ]
            """);
        File.WriteAllText(
            Path.Combine(second.SourceDirectory, "sei", "b.json"),
            """{"schemaVersion":2,"name":"b","versions":[{"version":"1.0.0","dependencies":[],"artifacts":null}]}""");

        // Mirror 'a' byte for byte so only the broken 'b' is left to report;
        // independently built zips can differ by their timestamps.
        var artifact = Directory.EnumerateFiles(
            Path.Combine(first.SourceDirectory, "artifacts"),
            "a-1.0.0-*.zip").Single();
        File.Copy(
            artifact,
            Path.Combine(second.SourceDirectory, "artifacts", Path.GetFileName(artifact)),
            overwrite: true);
        File.Copy(
            Path.Combine(first.SourceDirectory, "sei", "a.json"),
            Path.Combine(second.SourceDirectory, "sei", "a.json"),
            overwrite: true);

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(first.Root, first.StoreDirectory));

        Assert.Equal("checksum", error.Code);
        Assert.Contains("artifact list", error.Message);
    }

    private static void WriteConfig(Fixture first, Fixture second)
    {
        first.WriteConfig("win-x64");
        second.WriteConfig("win-x64");
        var firstIndex = Path.Combine(first.SourceDirectory, "index.json").Replace('\\', '/');
        var secondIndex = Path.Combine(second.SourceDirectory, "index.json").Replace('\\', '/');
        File.WriteAllText(
            Path.Combine(first.Root, "seiko.conf"),
            $$"""
            schemaVersion = {{SeikoConfig.CurrentSchemaVersion}}
            sources = [
              { id = "first", index = "{{firstIndex}}" },
              { id = "second", index = "{{secondIndex}}" }
            ]
            rid = "win-x64"
            sei = [
              { name = "hello", version = "[1.0.0]" }
            ]
            """);
    }
}
