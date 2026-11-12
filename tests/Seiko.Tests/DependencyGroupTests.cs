using System.Text.Json;
using Xunit;

namespace Seiko.Tests;

public sealed class DependencyGroupTests
{
    private static void AddPackage(Fixture fixture, string name)
        => fixture.AddSei(name, new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { [name + ".txt"] = name } }],
        });

    private static string FileOf(Fixture fixture, string name)
        => Path.Combine(fixture.Root, name + ".txt");

    private static void AddTwoGroupApp(Fixture fixture)
    {
        fixture.AddSei("app", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["app.txt"] = "app" },
                    DependencyGroups =
                    [
                        new GroupFixture(
                            Dependencies: [new DepFixture("bridge-a", "*")],
                            Prerequisites: [new DepFixture("host-a", "*")]),
                        new GroupFixture(
                            Dependencies: [new DepFixture("bridge-b", "*")],
                            Prerequisites: [new DepFixture("host-b", "*")]),
                    ],
                },
            ],
        });
    }

    [Fact]
    public void Sync_SelectsTheGroupWhosePrerequisitesHold()
    {
        using var fixture = new Fixture();
        foreach (var name in new[] { "host-a", "host-b", "bridge-a", "bridge-b" })
        {
            AddPackage(fixture, name);
        }

        AddTwoGroupApp(fixture);
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("app", "[1.0.0]"),
            new SpecFixture("host-a", "[1.0.0]"));

        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        Assert.True(File.Exists(FileOf(fixture, "bridge-a")));
        Assert.False(File.Exists(FileOf(fixture, "bridge-b")));
        var locked = client.ReadLock(fixture.Root)!.Sei.Single(sei => sei.Name == "app");
        Assert.Equal(0, locked.Artifact.Group);
        Assert.Contains(locked.Artifact.Dependencies, dependency => dependency.Name == "bridge-a");

        // The lock carries the merged scope and the group position, so the
        // same selection replays without re-deciding.
        client.Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen);
        Assert.True(File.Exists(FileOf(fixture, "bridge-a")));
    }

    [Fact]
    public void Sync_StacksGroupRelationsOnArtifactCommonRelations()
    {
        using var fixture = new Fixture();
        foreach (var name in new[] { "common", "marker", "scoped" })
        {
            AddPackage(fixture, name);
        }

        fixture.AddSei("app", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["app.txt"] = "app" },
                    Dependencies = [new DepFixture("common", "*")],
                    DependencyGroups =
                    [
                        new GroupFixture(
                            Dependencies: [new DepFixture("scoped", "*")],
                            Prerequisites: [new DepFixture("marker", "*")]),
                    ],
                },
            ],
        });
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("app", "[1.0.0]"),
            new SpecFixture("marker", "[1.0.0]"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.True(File.Exists(FileOf(fixture, "common")));
        Assert.True(File.Exists(FileOf(fixture, "scoped")));
    }

    [Fact]
    public void Sync_SelectsAGroupWhoseConflictIsAbsent()
    {
        using var fixture = new Fixture();
        AddPackage(fixture, "bridge");
        AddPackage(fixture, "legacy");
        fixture.AddSei("app", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["app.txt"] = "app" },
                    DependencyGroups =
                    [
                        new GroupFixture(
                            Dependencies: [new DepFixture("bridge", "*")],
                            Conflicts: [new DepFixture("legacy", "*")]),
                    ],
                },
            ],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("app", "[1.0.0]"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.True(File.Exists(FileOf(fixture, "bridge")));
    }

    [Fact]
    public void Sync_RejectsWhenEveryGroupIsExcluded()
    {
        using var fixture = new Fixture();
        AddPackage(fixture, "bridge");
        AddPackage(fixture, "legacy");
        fixture.AddSei("app", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["app.txt"] = "app" },
                    DependencyGroups =
                    [
                        new GroupFixture(
                            Dependencies: [new DepFixture("bridge", "*")],
                            Conflicts: [new DepFixture("legacy", "*")]),
                    ],
                },
            ],
        });
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("app", "[1.0.0]"),
            new SpecFixture("legacy", "[1.0.0]"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("resolve", error.Code);
        Assert.Contains("artifact conflict", error.Message);
        Assert.Contains("legacy", error.Message);
    }

    [Fact]
    public void Sync_PrefersAGroupThatIntroducesANewPrerequisite()
    {
        using var fixture = new Fixture();
        foreach (var name in new[] { "host", "extra", "base-bridge", "extra-bridge" })
        {
            AddPackage(fixture, name);
        }

        fixture.AddSei("app", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["app.txt"] = "app" },
                    Prerequisites = [new DepFixture("host", "*")],
                    DependencyGroups =
                    [
                        // Repeats an artifact-level prerequisite: no bonus.
                        new GroupFixture(
                            Dependencies: [new DepFixture("base-bridge", "*")],
                            Prerequisites: [new DepFixture("host", "*")]),
                        // Introduces a new prerequisite name: one bonus point.
                        new GroupFixture(
                            Dependencies: [new DepFixture("extra-bridge", "*")],
                            Prerequisites:
                            [
                                new DepFixture("host", "*"),
                                new DepFixture("extra", "*"),
                            ]),
                    ],
                },
            ],
        });
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("app", "[1.0.0]"),
            new SpecFixture("host", "[1.0.0]"),
            new SpecFixture("extra", "[1.0.0]"));

        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        Assert.True(File.Exists(FileOf(fixture, "extra-bridge")));
        Assert.False(File.Exists(FileOf(fixture, "base-bridge")));
        Assert.Equal(
            1,
            client.ReadLock(fixture.Root)!.Sei.Single(sei => sei.Name == "app").Artifact.Group);
    }

    [Fact]
    public void Sync_SelectsAnUnconditionalGroup()
    {
        using var fixture = new Fixture();
        AddPackage(fixture, "backend");
        fixture.AddSei("app", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["app.txt"] = "app" },
                    DependencyGroups =
                    [
                        new GroupFixture(Dependencies: [new DepFixture("backend", "*")]),
                    ],
                },
            ],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("app", "[1.0.0]"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.True(File.Exists(FileOf(fixture, "backend")));
    }

    [Fact]
    public void Sync_RejectsATieOnFirstInstall()
    {
        using var fixture = new Fixture();
        foreach (var name in new[] { "host-a", "host-b", "bridge-a", "bridge-b" })
        {
            AddPackage(fixture, name);
        }

        AddTwoGroupApp(fixture);
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("app", "[1.0.0]"),
            new SpecFixture("host-a", "[1.0.0]"),
            new SpecFixture("host-b", "[1.0.0]"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("resolve", error.Code);
        Assert.Contains("ambiguous artifact for app 1.0.0", error.Message);
    }

    [Fact]
    public void Sync_KeepsTheLockedGroupWhenAnotherBecomesEligible()
    {
        using var fixture = new Fixture();
        foreach (var name in new[] { "host-a", "host-b", "bridge-a", "bridge-b" })
        {
            AddPackage(fixture, name);
        }

        AddTwoGroupApp(fixture);
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("app", "[1.0.0]"),
            new SpecFixture("host-a", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        Assert.Equal(
            0,
            client.ReadLock(fixture.Root)!.Sei.Single(sei => sei.Name == "app").Artifact.Group);

        // Adding the second host makes both groups equally specific and
        // solvable; the lock decides instead of re-reporting ambiguity.
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("app", "[1.0.0]"),
            new SpecFixture("host-a", "[1.0.0]"),
            new SpecFixture("host-b", "[1.0.0]"));
        client.Sync(fixture.Root, fixture.StoreDirectory);

        Assert.True(File.Exists(FileOf(fixture, "bridge-a")));
        Assert.False(File.Exists(FileOf(fixture, "bridge-b")));
        Assert.Equal(
            0,
            client.ReadLock(fixture.Root)!.Sei.Single(sei => sei.Name == "app").Artifact.Group);
    }

    [Fact]
    public void Sync_RejectsGroupsThatTieAtTheGroupBonus()
    {
        using var fixture = new Fixture();
        foreach (var name in new[] { "host", "p1", "p2", "p3" })
        {
            AddPackage(fixture, name);
        }

        fixture.AddSei("app", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["app.txt"] = "app" },
                    Prerequisites = [new DepFixture("host", "*")],
                    DependencyGroups =
                    [
                        new GroupFixture(
                            Prerequisites:
                            [
                                new DepFixture("host", "*"),
                                new DepFixture("p1", "*"),
                            ]),
                        // Three new names still add only one point, so both
                        // groups tie at specificity 2.
                        new GroupFixture(
                            Prerequisites:
                            [
                                new DepFixture("host", "*"),
                                new DepFixture("p2", "*"),
                                new DepFixture("p3", "*"),
                            ]),
                    ],
                },
            ],
        });
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("app", "[1.0.0]"),
            new SpecFixture("host", "[1.0.0]"),
            new SpecFixture("p1", "[1.0.0]"),
            new SpecFixture("p2", "[1.0.0]"),
            new SpecFixture("p3", "[1.0.0]"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("resolve", error.Code);
        Assert.Contains("ambiguous artifact for app 1.0.0", error.Message);
        Assert.Contains("specificity 2", error.Message);
    }

    [Fact]
    public void DryRun_NamesTheSelectedGroup()
    {
        using var fixture = new Fixture();
        foreach (var name in new[] { "host-a", "host-b", "bridge-a", "bridge-b" })
        {
            AddPackage(fixture, name);
        }

        AddTwoGroupApp(fixture);
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("app", "[1.0.0]"),
            new SpecFixture("host-a", "[1.0.0]"));

        var result = new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory, dryRun: true);

        Assert.Contains(
            result.Preview!.Diagnostics,
            diagnostic => diagnostic.Kind == "artifact-group"
                && diagnostic.Message.Contains("#0", StringComparison.Ordinal));
    }

    [Fact]
    public void Sync_RejectsDependencyGroupsOnAPreV4Manifest()
    {
        using var fixture = new Fixture();
        AddPackage(fixture, "marker");
        fixture.AddSei("app", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["app.txt"] = "app" },
                    DependencyGroups =
                    [
                        new GroupFixture(Prerequisites: [new DepFixture("marker", "*")]),
                    ],
                    ManifestSchemaVersion = 3,
                },
            ],
        });
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("app", "[1.0.0]"),
            new SpecFixture("marker", "[1.0.0]"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Contains("schemaVersion 3 cannot declare dependencyGroups", error.Message);
    }

    [Fact]
    public void Verify_AllowsAnUnconditionalGroup()
    {
        using var fixture = new Fixture();
        AddPackage(fixture, "backend");
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["hello.txt"] = "hello" },
                    DependencyGroups =
                    [
                        new GroupFixture(Dependencies: [new DepFixture("backend", "*")]),
                    ],
                },
            ],
        });
        fixture.WriteConfig("win-x64");

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Verify_RejectsGroupsWithTheSameConditionsAndDifferentRelations()
    {
        using var fixture = new Fixture();
        foreach (var name in new[] { "backend-a", "backend-b" })
        {
            AddPackage(fixture, name);
        }

        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["hello.txt"] = "hello" },
                    DependencyGroups =
                    [
                        // Both unconditional, different installs: source or
                        // environment drift would silently pick a backend.
                        new GroupFixture(Dependencies: [new DepFixture("backend-a", "*")]),
                        new GroupFixture(Dependencies: [new DepFixture("backend-b", "*")]),
                    ],
                },
            ],
        });
        fixture.WriteConfig("win-x64");

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding =>
            finding.Code == "artifact-group-ambiguous"
            && finding.Message.Contains("same conditions", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_AllowsGroupsDistinguishedByConflicts()
    {
        using var fixture = new Fixture();
        foreach (var name in new[] { "backend-a", "backend-b", "legacy" })
        {
            AddPackage(fixture, name);
        }

        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["hello.txt"] = "hello" },
                    DependencyGroups =
                    [
                        new GroupFixture(
                            Dependencies: [new DepFixture("backend-a", "*")],
                            Conflicts: [new DepFixture("legacy", "*")]),
                        new GroupFixture(Dependencies: [new DepFixture("backend-b", "*")]),
                    ],
                },
            ],
        });
        fixture.WriteConfig("win-x64");

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Verify_RejectsDependencyGroupsBelowSchemaFour()
    {
        using var fixture = new Fixture();
        AddPackage(fixture, "marker");
        fixture.AddSei("app", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["app.txt"] = "app" },
                    DependencyGroups =
                    [
                        new GroupFixture(Prerequisites: [new DepFixture("marker", "*")]),
                    ],
                    ManifestSchemaVersion = 3,
                },
            ],
        });
        fixture.WriteConfig("win-x64");

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding =>
            finding.Code == "manifest-group"
            && finding.Message.Contains("schemaVersion 4", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_RejectsGroupsThatDifferFromTheManifest()
    {
        using var fixture = new Fixture();
        foreach (var name in new[] { "host-a", "host-b", "bridge-a", "bridge-b" })
        {
            AddPackage(fixture, name);
        }

        AddTwoGroupApp(fixture);
        fixture.WriteConfig("win-x64");

        var path = Path.Combine(fixture.SourceDirectory, "sei", "app.json");
        var sei = JsonSerializer.Deserialize(
            File.ReadAllBytes(path),
            SeikoJsonContext.Default.SeiIndex)!;
        sei.Versions[0].Artifacts[0].DependencyGroups = null;
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(sei, SeikoJsonContext.Default.SeiIndex));
        fixture.RefreshCatalog();

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding =>
            finding.Code == "manifest-mismatch"
            && finding.Message.Contains("dependencyGroups", StringComparison.Ordinal));
    }

    [Fact]
    public void Frozen_ReplaysAnOriginGroupSelection()
    {
        using var fixture = new Fixture();
        AddPackage(fixture, "host-a");
        var origin = fixture.WriteOrigin(
            "app",
            "1.0.0",
            "app.txt",
            "app",
            groups:
            [
                new GroupFixture(
                    Dependencies: [new DepFixture("host-a", "*")],
                    Prerequisites: [new DepFixture("host-a", "*")]),
            ]);
        fixture.WriteConfig("win-x64", new SpecFixture("host-a", "[1.0.0]"));
        var client = new SeikoClient();

        client.Add(
            fixture.Root,
            Path.GetRelativePath(
                Directory.GetCurrentDirectory(),
                Path.Combine(fixture.Root, origin)),
            storeRoot: fixture.StoreDirectory);

        var locked = client.ReadLock(fixture.Root)!.Sei.Single(sei => sei.Name == "app");
        Assert.Equal(0, locked.Artifact.Group);

        // Replay rebuilds the artifact-level share from the merged lock and
        // merges the same group again; the selection must come back identical.
        client.Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen);

        Assert.Equal("app", File.ReadAllText(Path.Combine(fixture.Root, "app.txt")));
    }

    [Fact]
    public void Frozen_RejectsAReorderedGroupList()
    {
        using var fixture = new Fixture();
        foreach (var name in new[] { "host-a", "host-b", "bridge-a", "bridge-b" })
        {
            AddPackage(fixture, name);
        }

        AddTwoGroupApp(fixture);
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("app", "[1.0.0]"),
            new SpecFixture("host-a", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        // Same groups, different positions: the lock's index must pin the
        // selection instead of re-deciding from merged relations alone.
        var path = Path.Combine(fixture.SourceDirectory, "sei", "app.json");
        var sei = JsonSerializer.Deserialize(
            File.ReadAllBytes(path),
            SeikoJsonContext.Default.SeiIndex)!;
        var groups = sei.Versions[0].Artifacts[0].DependencyGroups!;
        groups.Reverse();
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(sei, SeikoJsonContext.Default.SeiIndex));
        fixture.RefreshCatalog();

        var error = Assert.Throws<SeikoException>(
            () => client.Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen));

        Assert.Contains("artifact metadata of app 1.0.0 changed", error.Message);
    }
}
