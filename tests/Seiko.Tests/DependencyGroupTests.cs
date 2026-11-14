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
    public void Sync_DoesNotCallAGroupSolvableWhenItDropsOutOfTheGraph()
    {
        using var fixture = new Fixture();
        foreach (var name in new[] { "host", "y" })
        {
            AddPackage(fixture, name);
        }

        // app's high group pulls x; its low group does not. x's first group
        // depends on a missing package, the second on y. Only the second can
        // hold while app stays on its high group.
        fixture.AddSei("x", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["x.txt"] = "x" },
                    DependencyGroups =
                    [
                        new GroupFixture(
                            Dependencies: [new DepFixture("missing", "*")],
                            Prerequisites: [new DepFixture("host", "*")]),
                        new GroupFixture(
                            Dependencies: [new DepFixture("y", "*")],
                            Prerequisites: [new DepFixture("host", "*")]),
                    ],
                },
            ],
        });
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
                            Dependencies: [new DepFixture("x", "*")],
                            Prerequisites: [new DepFixture("host", "*")]),
                        new GroupFixture(),
                    ],
                },
            ],
        });
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("app", "[1.0.0]"),
            new SpecFixture("host", "[1.0.0]"));

        // Probing x's dead group may fall back to app's low group, which makes
        // x disappear; the probe must not read that as "this group works".
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.True(File.Exists(FileOf(fixture, "x")));
        Assert.True(File.Exists(FileOf(fixture, "y")));
    }

    [Fact]
    public void Sync_ReportsAmbiguityWhenTheLockedGroupIsBelowTheTopLayer()
    {
        using var fixture = new Fixture();
        AddPackage(fixture, "host");
        fixture.AddSei("common", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["common.txt"] = "c1" } }],
        });

        // aaa is declared first, so its high group pins common@1 before app is
        // reached and app's high groups fail in the current graph. Their own
        // probes can drop aaa's high group, so both are solvable there.
        fixture.AddSei("aaa", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["aaa.txt"] = "aaa" },
                    DependencyGroups =
                    [
                        new GroupFixture(),
                        new GroupFixture(
                            Dependencies: [new DepFixture("common", "[1.0.0]")],
                            Prerequisites: [new DepFixture("host", "*")]),
                    ],
                },
            ],
        });
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
                        new GroupFixture(),
                        new GroupFixture(
                            Dependencies: [new DepFixture("common", "[2.0.0]")],
                            Prerequisites: [new DepFixture("host", "*")]),
                        new GroupFixture(
                            Dependencies: [new DepFixture("common", "[3.0.0]")],
                            Prerequisites: [new DepFixture("host", "*")]),
                    ],
                },
            ],
        });
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("aaa", "[1.0.0]"),
            new SpecFixture("app", "[1.0.0]"),
            new SpecFixture("host", "[1.0.0]"));
        var client = new SeikoClient();

        // Only common 1.0.0 exists, so both specific groups are unsolvable
        // and the unconditional group becomes the lock.
        client.Sync(fixture.Root, fixture.StoreDirectory);
        Assert.Equal(
            0,
            client.ReadLock(fixture.Root)!.Sei.Single(sei => sei.Name == "app").Artifact.Group);

        // Later versions make both specific groups solvable in their own
        // graphs. The lock group sits below the top layer, so it must not
        // silence the tie between them.
        var path = Path.Combine(fixture.SourceDirectory, "sei", "common.json");
        var common = JsonSerializer.Deserialize(
            File.ReadAllBytes(path),
            SeikoJsonContext.Default.SeiIndex)!;
        foreach (var version in new[] { "2.0.0", "3.0.0" })
        {
            common.Versions.Add(new SeiVersionEntry
            {
                Version = version,
                Artifacts =
                [
                    new ArtifactEntry
                    {
                        Layout = "root",
                        Source = common.Versions[0].Artifacts[0].Source,
                    },
                ],
            });
        }

        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(common, SeikoJsonContext.Default.SeiIndex));
        fixture.RefreshCatalog();

        var error = Assert.Throws<SeikoException>(
            () => client.Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("resolve", error.Code);
        Assert.Contains("ambiguous artifact for app 1.0.0", error.Message);
    }

    private static Fixture OrderedRoots()
    {
        var fixture = new Fixture();
        AddPackage(fixture, "host");

        // Two common versions so each root's high group can hold, but the two
        // groups want different ones.
        fixture.AddSei(
            "common",
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts = [new ArtifactFixture { Files = { ["one.txt"] = "one" } }],
            },
            new VersionFixture
            {
                Version = "2.0.0",
                Artifacts = [new ArtifactFixture { Files = { ["two.txt"] = "two" } }],
            });

        fixture.AddSei("aa", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["aa.txt"] = "aa" },
                    DependencyGroups =
                    [
                        new GroupFixture(),
                        new GroupFixture(
                            Dependencies: [new DepFixture("common", "[1.0.0]")],
                            Prerequisites: [new DepFixture("host", "*")]),
                    ],
                },
            ],
        });
        fixture.AddSei("bb", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["bb.txt"] = "bb" },
                    DependencyGroups =
                    [
                        new GroupFixture(),
                        new GroupFixture(
                            Dependencies: [new DepFixture("common", "[2.0.0]")],
                            Prerequisites: [new DepFixture("host", "*")]),
                    ],
                },
            ],
        });
        return fixture;
    }

    private static List<(string Name, int? Group)> Roots(Fixture fixture, SeikoClient client)
        => client.ReadLock(fixture.Root)!.Sei
            .Where(sei => sei.Name is "aa" or "bb")
            .Select(sei => (sei.Name, sei.Artifact.Group))
            .ToList();

    [Fact]
    public void Sync_DoesNotDependOnRootDeclarationOrder()
    {
        using var first = OrderedRoots();
        first.WriteConfig(
            "win-x64",
            new SpecFixture("aa", "[1.0.0]"),
            new SpecFixture("bb", "[1.0.0]"),
            new SpecFixture("host", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(first.Root, first.StoreDirectory);

        using var second = OrderedRoots();
        second.WriteConfig(
            "win-x64",
            new SpecFixture("host", "[1.0.0]"),
            new SpecFixture("bb", "[1.0.0]"),
            new SpecFixture("aa", "[1.0.0]"));
        client.Sync(second.Root, second.StoreDirectory);

        Assert.Equal(Roots(first, client), Roots(second, client));
    }

    [Fact]
    public void Remove_DoesNotSwitchGroupsSilently()
    {
        using var fixture = new Fixture();
        foreach (var name in new[] { "a", "b", "x", "y" })
        {
            AddPackage(fixture, name);
        }

        // Group 0 holds while a is installed; removing a makes group 1 the
        // only one left, with a different set of sei behind it.
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
                            Dependencies: [new DepFixture("x", "*")],
                            Prerequisites: [new DepFixture("a", "*")]),
                        new GroupFixture(
                            Dependencies: [new DepFixture("y", "*")],
                            Prerequisites: [new DepFixture("b", "*")]),
                    ],
                },
            ],
        });
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("app", "[1.0.0]"),
            new SpecFixture("a", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        Assert.Equal(
            0,
            client.ReadLock(fixture.Root)!.Sei.Single(sei => sei.Name == "app").Artifact.Group);

        // b joins after group 0 is locked; on its own, group 1 would now be
        // the only eligible one, but the lock keeps 0 and the tie is silent.
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("app", "[1.0.0]"),
            new SpecFixture("a", "[1.0.0]"),
            new SpecFixture("b", "[1.0.0]"));

        // Version, digest and rid all stay the same; only the group changes,
        // and with it the installed set (x out, y in).
        var error = Assert.Throws<SeikoException>(
            () => client.Remove(fixture.Root, "a", storeRoot: fixture.StoreDirectory));

        Assert.Equal("resolve", error.Code);
        Assert.Contains("app", error.Message);
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
    public void Verify_TreatsRepeatedPrerequisitesAsTheSameCondition()
    {
        using var fixture = new Fixture();
        foreach (var name in new[] { "a", "x", "y" })
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
                        // The same condition written twice: [a] and [a, a]
                        // gate identically, so these two are indistinguishable.
                        new GroupFixture(
                            Dependencies: [new DepFixture("x", "*")],
                            Prerequisites: [new DepFixture("a", "*")]),
                        new GroupFixture(
                            Dependencies: [new DepFixture("y", "*")],
                            Prerequisites:
                            [
                                new DepFixture("a", "*"),
                                new DepFixture("a", "*"),
                            ]),
                    ],
                },
            ],
        });
        fixture.WriteConfig("win-x64");

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding => finding.Code == "artifact-group-ambiguous");
    }

    [Fact]
    public void Add_RejectsUnconditionalOriginGroupsThatInstallDifferentSei()
    {
        using var fixture = new Fixture();
        foreach (var name in new[] { "x", "y" })
        {
            AddPackage(fixture, name);
        }

        // Two unconditional origin groups: an indexed artifact of the same
        // shape is rejected at packaging time, so a local file must not slip
        // through and let a failing dependency decide.
        var origin = fixture.WriteOrigin(
            "app",
            "1.0.0",
            "app.txt",
            "app",
            groups:
            [
                new GroupFixture(Dependencies: [new DepFixture("x", "*")]),
                new GroupFixture(Dependencies: [new DepFixture("y", "*")]),
            ]);
        fixture.WriteConfig("win-x64", new SpecFixture("x", "[1.0.0]"));

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root,
            Path.GetRelativePath(
                Directory.GetCurrentDirectory(),
                Path.Combine(fixture.Root, origin)),
            storeRoot: fixture.StoreDirectory));

        Assert.Contains("same conditions", error.Message);
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
    public void Frozen_AcceptsRepeatedNamesWithDifferentRanges()
    {
        using var fixture = new Fixture();
        AddPackage(fixture, "host");
        var origin = fixture.WriteOrigin(
            "app",
            "1.0.0",
            "app.txt",
            "app",
            groups:
            [
                // The group repeats one of the artifact-level ranges of the
                // same name.
                new GroupFixture(
                    Dependencies: [new DepFixture("host", "[1.0.0,4.0.0)")],
                    Prerequisites: [new DepFixture("host", "*")]),
            ],
            // Declared high range first; the lock sorts low range first, so
            // replaying rebuilds the artifact-level share in the other order.
            dependencies: [("host", "[1.0.0,4.0.0)"), ("host", "[0.0.0,4.0.0)")],
            dependencyLevel: "artifact");
        fixture.WriteConfig("win-x64", new SpecFixture("host", "[1.0.0]"));
        var client = new SeikoClient();

        client.Add(
            fixture.Root,
            Path.GetRelativePath(
                Directory.GetCurrentDirectory(),
                Path.Combine(fixture.Root, origin)),
            storeRoot: fixture.StoreDirectory);

        // Lock entries are written sorted by range, so the group's range can
        // land before the artifact-level one even though it was declared
        // second. The replay rebuilds the artifact-level share by removing
        // the group's share from that sorted list, so the result is ordered
        // differently from the manifest; comparing by position alone would
        // call that a change.
        client.Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen);
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
