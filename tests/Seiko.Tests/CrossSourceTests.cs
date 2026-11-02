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
