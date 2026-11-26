using Xunit;

namespace Seiko.Tests;

public sealed class CrossSourceTests
{
    [Fact]
    public void Sync_RejectsDifferentDigestsForEquivalentArtifacts()
    {
        using var first = new Fixture();
        first.AddSei(
            "hello",
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "first" } }],
            }
        );
        using var second = new Fixture();
        second.AddSei(
            "hello",
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "second" } }],
            }
        );
        WriteConfig(first, second);

        var error = Assert.Throws<SeikoException>(() =>
            new SeikoClient().Sync(first.Root, first.StoreDirectory)
        );

        Assert.Equal("checksum", error.Code);
        Assert.Contains("differs between", error.Message);
    }

    [Fact]
    public void Sync_AllowsDifferentDigestsForDifferentConditions()
    {
        using var first = new Fixture();
        first.AddSei(
            "hello",
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "first" } }],
            }
        );
        using var second = new Fixture();
        second.AddSei(
            "hello",
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts =
                [
                    new ArtifactFixture { Rid = "linux-x64", Files = { ["hello.txt"] = "second" } },
                ],
            }
        );
        WriteConfig(first, second);

        new SeikoClient().Sync(first.Root, first.StoreDirectory);

        Assert.Equal("first", File.ReadAllText(Path.Combine(first.Root, "hello.txt")));
    }

    [Fact]
    public void Sync_RejectsDifferentDigestsWhenTheOnlyDifferenceIsGroupOrder()
    {
        using var first = new Fixture();
        first.AddSei(
            "host-a",
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts = [new ArtifactFixture { Files = { ["expected-a.txt"] = "host-a" } }],
            }
        );
        first.AddSei(
            "host-b",
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts = [new ArtifactFixture { Files = { ["expected-b.txt"] = "host-b" } }],
            }
        );
        first.AddSei(
            "hello",
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts =
                [
                    new ArtifactFixture
                    {
                        Files = { ["hello.txt"] = "first" },
                        // host-a is a common gate and scores on its own; only the
                        // group that adds host-b earns the extra point, so the
                        // first group is strictly more specific and no tie is
                        // reported.
                        Prerequisites = [new DepFixture("host-a", "*")],
                        DependencyGroups =
                        [
                            new GroupFixture(),
                            new GroupFixture(Prerequisites: [new DepFixture("host-b", "*")]),
                        ],
                    },
                ],
            }
        );
        using var second = new Fixture();
        foreach (var name in new[] { "host-a", "host-b" })
        {
            second.AddSei(
                name,
                new VersionFixture
                {
                    Version = "1.0.0",
                    Artifacts =
                    [
                        new ArtifactFixture { Files = { [$"expected-{name}.txt"] = name } },
                    ],
                }
            );
        }

        second.AddSei(
            "hello",
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts =
                [
                    new ArtifactFixture
                    {
                        Files = { ["hello.txt"] = "second" },
                        // Same gates, other order: the environments that pick
                        // this artifact are identical, so the bytes must match.
                        Prerequisites = [new DepFixture("host-a", "*")],
                        DependencyGroups =
                        [
                            new GroupFixture(Prerequisites: [new DepFixture("host-b", "*")]),
                            new GroupFixture(),
                        ],
                    },
                ],
            }
        );

        // The hosts are copied byte for byte from the first source, so the
        // only thing that can differ is hello itself; otherwise their digests
        // would trigger the same check and hide whether the group order did.
        foreach (var name in new[] { "host-a", "host-b" })
        {
            var artifact = Directory
                .EnumerateFiles(
                    Path.Combine(first.SourceDirectory, "artifacts"),
                    $"{name}-1.0.0-*.zip"
                )
                .Single();
            File.Copy(
                artifact,
                Path.Combine(second.SourceDirectory, "artifacts", Path.GetFileName(artifact)),
                overwrite: true
            );
            File.Copy(
                Path.Combine(first.SourceDirectory, "sei", $"{name}.json"),
                Path.Combine(second.SourceDirectory, "sei", $"{name}.json"),
                overwrite: true
            );
        }

        WriteConfig(first, second, "hello", "host-a", "host-b");

        var error = Assert.Throws<SeikoException>(() =>
            new SeikoClient().Sync(first.Root, first.StoreDirectory)
        );

        Assert.Equal("checksum", error.Code);
        Assert.Contains("differs between", error.Message);
    }

    [Fact]
    public void Sync_ValidatesEveryMirroredIndexObject()
    {
        using var first = new Fixture();
        first.AddSei(
            "a",
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts = [new ArtifactFixture { Files = { ["a.txt"] = "a" } }],
            }
        );
        first.AddSei(
            "b",
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts = [new ArtifactFixture { Files = { ["b.txt"] = "b" } }],
            }
        );
        using var second = new Fixture();
        second.AddSei(
            "a",
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts = [new ArtifactFixture { Files = { ["a.txt"] = "a" } }],
            }
        );
        second.AddSei(
            "b",
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts = [new ArtifactFixture { Files = { ["b.txt"] = "b" } }],
            }
        );
        first.WriteConfig(
            "win-x64",
            new SpecFixture("a", "[1.0.0]"),
            new SpecFixture("b", "[1.0.0]")
        );
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
            """
        );
        File.WriteAllText(
            Path.Combine(second.SourceDirectory, "sei", "b.json"),
            """{"schemaVersion":2,"name":"b","versions":[{"version":"1.0.0","dependencies":[],"artifacts":null}]}"""
        );

        // Mirror 'a' byte for byte so only the broken 'b' is left to report;
        // independently built zips can differ by their timestamps.
        var artifact = Directory
            .EnumerateFiles(Path.Combine(first.SourceDirectory, "artifacts"), "a-1.0.0-*.zip")
            .Single();
        File.Copy(
            artifact,
            Path.Combine(second.SourceDirectory, "artifacts", Path.GetFileName(artifact)),
            overwrite: true
        );
        File.Copy(
            Path.Combine(first.SourceDirectory, "sei", "a.json"),
            Path.Combine(second.SourceDirectory, "sei", "a.json"),
            overwrite: true
        );

        var error = Assert.Throws<SeikoException>(() =>
            new SeikoClient().Sync(first.Root, first.StoreDirectory)
        );

        Assert.Equal("checksum", error.Code);
        Assert.Contains("artifact list", error.Message);
    }

    private static void WriteConfig(Fixture first, Fixture second, params string[] roots)
    {
        first.WriteConfig("win-x64");
        second.WriteConfig("win-x64");
        var firstIndex = Path.Combine(first.SourceDirectory, "index.json").Replace('\\', '/');
        var secondIndex = Path.Combine(second.SourceDirectory, "index.json").Replace('\\', '/');
        var specs = string.Join(
            ",\n",
            (roots.Length == 0 ? ["hello"] : roots).Select(root =>
                $"  {{ name = \"{root}\", version = \"[1.0.0]\" }}"
            )
        );
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
            {{specs}}
            ]
            """
        );
    }
}
