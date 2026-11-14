using System.Text.Json;
using Xunit;

namespace Seiko.Tests;

public sealed class PreserveTests
{
    private const string ConfigPath = "config/server.properties";

    private static string Config(Fixture fixture)
        => Path.Combine(fixture.Root, "config", "server.properties");

    private static LocalState ReadState(Fixture fixture)
        => JsonSerializer.Deserialize(
            File.ReadAllBytes(Path.Combine(fixture.Root, ".seiko", "state.json")),
            SeikoJsonContext.Default.LocalState)!;

    private static Fixture PreservingFixture(string content = "default")
    {
        var fixture = new Fixture();
        fixture.AddSei("app", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { [ConfigPath] = content },
                    Preserve = [ConfigPath],
                },
            ],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("app", "[1.0.0]"));
        return fixture;
    }

    [Fact]
    public void Sync_KeepsAnExistingFileAtAPreservePath()
    {
        using var fixture = PreservingFixture();
        var target = Config(fixture);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "user");

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("user", File.ReadAllText(target));
        Assert.DoesNotContain(ConfigPath, ReadState(fixture).Files.Keys);
    }

    [Fact]
    public void Sync_WritesAPreservePathOnceAndThenLeavesItAlone()
    {
        using var fixture = PreservingFixture();
        var client = new SeikoClient();

        client.Sync(fixture.Root, fixture.StoreDirectory);

        var target = Config(fixture);
        Assert.Equal("default", File.ReadAllText(target));
        Assert.DoesNotContain(ConfigPath, ReadState(fixture).Files.Keys);

        // In-place edit: a hardlink to the store would corrupt it, so the
        // second sync also proves the file is an independent copy.
        File.WriteAllText(target, "edited");
        client.Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("edited", File.ReadAllText(target));
        Assert.DoesNotContain(ConfigPath, ReadState(fixture).Files.Keys);
    }

    [Fact]
    public void Sync_ForceDoesNotOverrideAPreserveFile()
    {
        using var fixture = PreservingFixture();
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var target = Config(fixture);
        File.WriteAllText(target, "edited");

        client.Sync(fixture.Root, fixture.StoreDirectory, force: true);

        Assert.Equal("edited", File.ReadAllText(target));
    }

    [Fact]
    public void Sync_ResetPreservedReplacesTheFileAndManagesIt()
    {
        using var fixture = PreservingFixture();
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var target = Config(fixture);
        File.WriteAllText(target, "edited");

        client.Sync(fixture.Root, fixture.StoreDirectory, resetPreserved: true);

        Assert.Equal("default", File.ReadAllText(target));
        Assert.Contains(ConfigPath, ReadState(fixture).Files.Keys);

        // A managed file is the package's from now on: replacing it (and so
        // breaking the store link) is a deployment conflict.
        File.Delete(target);
        File.WriteAllText(target, "edited again");
        var error = Assert.Throws<SeikoException>(
            () => client.Sync(fixture.Root, fixture.StoreDirectory));
        Assert.Equal("deploy", error.Code);
        Assert.Contains("was modified", error.Message);
    }

    [Fact]
    public void Sync_ResetPreservedLeavesOrdinaryCollisionsAlone()
    {
        using var fixture = new Fixture();
        fixture.AddSei("app", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files =
                    {
                        ["plain.txt"] = "package",
                        [ConfigPath] = "default",
                    },
                    Preserve = [ConfigPath],
                },
            ],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("app", "[1.0.0]"));
        File.WriteAllText(Path.Combine(fixture.Root, "plain.txt"), "user");

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Sync(
            fixture.Root,
            fixture.StoreDirectory,
            resetPreserved: true));

        Assert.Equal("deploy", error.Code);
        Assert.Contains("plain.txt", error.Message);
    }

    [Fact]
    public void Remove_LeavesAPreserveFileInPlace()
    {
        using var fixture = PreservingFixture();
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var target = Config(fixture);
        File.WriteAllText(target, "edited");

        client.Remove(fixture.Root, "app", storeRoot: fixture.StoreDirectory);

        Assert.Equal("edited", File.ReadAllText(target));
    }

    [Fact]
    public void Sync_RejectsPreserveOnAPreV4Manifest()
    {
        using var fixture = new Fixture();
        fixture.AddSei("app", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { [ConfigPath] = "default" },
                    Preserve = [ConfigPath],
                    ManifestSchemaVersion = 3,
                },
            ],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("app", "[1.0.0]"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Contains("schemaVersion 3 cannot declare preserve", error.Message);
    }

    [Fact]
    public void Sync_KeepsTheExistingFileWhenAnUpgradeAddsPreserve()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "app",
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts = [new ArtifactFixture { Files = { [ConfigPath] = "v1" } }],
            },
            new VersionFixture
            {
                Version = "1.1.0",
                Artifacts =
                [
                    new ArtifactFixture
                    {
                        Files = { [ConfigPath] = "v2" },
                        Preserve = [ConfigPath],
                    },
                ],
            });
        fixture.WriteConfig("win-x64", new SpecFixture("app", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        fixture.WriteConfig("win-x64", new SpecFixture("app", "[1.1.0]"));
        client.Sync(fixture.Root, fixture.StoreDirectory);

        // The declaration is what decides: the path is preserve now, so the
        // content that exists wins and the path leaves the managed state.
        Assert.Equal("v1", File.ReadAllText(Config(fixture)));
        Assert.DoesNotContain(ConfigPath, ReadState(fixture).Files.Keys);
    }

    [Fact]
    public void Sync_ResetPreservedReplacesAnUpgradedPreservePath()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "app",
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts = [new ArtifactFixture { Files = { [ConfigPath] = "v1" } }],
            },
            new VersionFixture
            {
                Version = "1.1.0",
                Artifacts =
                [
                    new ArtifactFixture
                    {
                        Files = { [ConfigPath] = "v2" },
                        Preserve = [ConfigPath],
                    },
                ],
            });
        fixture.WriteConfig("win-x64", new SpecFixture("app", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        fixture.WriteConfig("win-x64", new SpecFixture("app", "[1.1.0]"));
        client.Sync(fixture.Root, fixture.StoreDirectory, resetPreserved: true);

        Assert.Equal("v2", File.ReadAllText(Config(fixture)));
        Assert.Contains(ConfigPath, ReadState(fixture).Files.Keys);
    }

    [Fact]
    public void Rollback_LeavesAPreservePathTheTargetGenerationDidNotManage()
    {
        using var fixture = PreservingFixture();
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var target = Config(fixture);
        File.WriteAllText(target, "edited");
        client.Sync(fixture.Root, fixture.StoreDirectory, resetPreserved: true);
        Assert.Equal("default", File.ReadAllText(target));

        File.Delete(target);
        File.WriteAllText(target, "edited again");
        client.Rollback(fixture.Root, fixture.StoreDirectory, toGeneration: 1);

        Assert.Equal("edited again", File.ReadAllText(target));
        Assert.DoesNotContain(ConfigPath, ReadState(fixture).Files.Keys);
    }

    [Fact]
    public void Rollback_RestoresAPreservePathTheTargetGenerationManaged()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "app",
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts =
                [
                    new ArtifactFixture
                    {
                        Files = { [ConfigPath] = "one", ["other.txt"] = "other" },
                        Preserve = [ConfigPath],
                    },
                ],
            },
            new VersionFixture
            {
                Version = "1.1.0",
                Artifacts = [new ArtifactFixture { Files = { ["other.txt"] = "other" } }],
            });
        fixture.WriteConfig("win-x64", new SpecFixture("app", "[1.0.0]"));
        var client = new SeikoClient();

        // Generation 1 preserves the file; generation 2 resets it to managed.
        client.Sync(fixture.Root, fixture.StoreDirectory);
        client.Sync(fixture.Root, fixture.StoreDirectory, resetPreserved: true);
        Assert.Contains(ConfigPath, ReadState(fixture).Files.Keys);

        // Generation 3 drops the file: managed and unmodified, so it goes.
        fixture.WriteConfig("win-x64", new SpecFixture("app", "[1.1.0]"));
        client.Sync(fixture.Root, fixture.StoreDirectory);
        Assert.False(File.Exists(Config(fixture)));
        Assert.DoesNotContain(ConfigPath, ReadState(fixture).Files.Keys);

        // Generation 2 managed the path, so the rollback restores it as
        // managed instead of treating it as a preserve file it never owned.
        client.Rollback(fixture.Root, fixture.StoreDirectory, toGeneration: 2);

        Assert.Equal("one", File.ReadAllText(Config(fixture)));
        Assert.Contains(ConfigPath, ReadState(fixture).Files.Keys);
    }

    [Fact]
    public void DryRun_ListsPreservePathsWithoutAddingChangeEntries()
    {
        using var fixture = PreservingFixture();

        var result = new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory, dryRun: true);

        var preserved = Assert.Single(result.Preview!.Preserved);
        Assert.Equal("app", preserved.Name);
        Assert.Equal(ConfigPath, preserved.Path);
        var change = Assert.Single(result.Preview.Changes);
        Assert.Equal("add", change.Action);
    }

    [Fact]
    public void DryRunPrintsPreservePathsInHumanOutput()
    {
        using var fixture = PreservingFixture();

        var (stdout, stderr, exitCode) = Cli.Run(
            ["sync", "--dry-run", "--root", fixture.Root, "--store", fixture.StoreDirectory]);

        Assert.Equal(0, exitCode);
        Assert.Contains($"preserve {ConfigPath} (app)", stdout + stderr);
    }

    [Fact]
    public void Verify_AcceptsMatchingPreserve()
    {
        using var fixture = PreservingFixture();
        fixture.RefreshCatalog();

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Empty(result.Findings);
        Assert.Equal(1, result.CheckedArtifacts);
    }

    [Fact]
    public void Verify_RejectsPreserveOnAPreV4Manifest()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["hello.txt"] = "hello" },
                    Preserve = ["hello.txt"],
                    ManifestSchemaVersion = 3,
                },
            ],
        });
        fixture.WriteConfig("win-x64");

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding =>
            finding.Code == "manifest-preserve"
            && finding.Message.Contains("schemaVersion 4", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_RejectsAPreservePathTheArtifactDoesNotDeploy()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["hello.txt"] = "hello" },
                    Preserve = ["nope.txt"],
                },
            ],
        });
        fixture.WriteConfig("win-x64");

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding =>
            finding.Code == "manifest-preserve"
            && finding.Message.Contains("nope.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_RejectsPreserveThatDiffersFromTheIndex()
    {
        using var fixture = PreservingFixture();
        var sei = JsonSerializer.Deserialize(
            File.ReadAllBytes(Path.Combine(fixture.SourceDirectory, "sei", "app.json")),
            SeikoJsonContext.Default.SeiIndex)!;
        sei.Versions[0].Artifacts[0].Preserve = null;
        File.WriteAllBytes(
            Path.Combine(fixture.SourceDirectory, "sei", "app.json"),
            JsonSerializer.SerializeToUtf8Bytes(sei, SeikoJsonContext.Default.SeiIndex));
        fixture.RefreshCatalog();

        var result = IndexVerifier.Verify(fixture.SourceDirectory, checkArtifacts: true);

        Assert.Contains(result.Findings, finding =>
            finding.Code == "manifest-mismatch"
            && finding.Message.Contains("preserve", StringComparison.Ordinal));
    }
}
