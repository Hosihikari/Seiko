using System.Text.Json;
using Xunit;

namespace Seiko.Tests;

public sealed class DeployTransactionTests
{
    [Fact]
    public void Sync_RollsBackPartialDeployment()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture
            {
                Files =
                {
                    ["a.txt"] = "one-a",
                    ["b.txt"] = "one-b",
                },
            }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture
            {
                Files =
                {
                    ["a.txt"] = "two-a",
                    ["b.txt"] = "two-b",
                },
            }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));

        try
        {
            Deployer.AppliedForTests.Value = count =>
            {
                if (count == 1)
                {
                    throw new IOException("injected deployment failure");
                }
            };
            Assert.Throws<IOException>(
                () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));
        }
        finally
        {
            Deployer.AppliedForTests.Value = null;
        }

        Assert.Equal("one-a", File.ReadAllText(Path.Combine(fixture.Root, "a.txt")));
        Assert.Equal("one-b", File.ReadAllText(Path.Combine(fixture.Root, "b.txt")));
        Assert.Equal("1.0.0", new SeikoClient().ReadLock(fixture.Root)!.Sei.Single().Version);
        var state = JsonSerializer.Deserialize(
            File.ReadAllBytes(Path.Combine(fixture.Root, ".seiko", "state.json")),
            SeikoJsonContext.Default.LocalState);
        Assert.Equal(1, state!.Generation);
        Assert.False(File.Exists(Path.Combine(fixture.Root, ".seiko", "transaction.json")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".seiko", "transaction")));
    }

    [Fact]
    public void Sync_RejectsFileAndDirectoryPrefixConflictBeforeWriting()
    {
        using var fixture = new Fixture();
        fixture.AddSei("a", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["bin/tool"] = "tool" } }],
        });
        fixture.AddSei("b", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["bin"] = "flat" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("a"), new SpecFixture("b"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("deploy", error.Code);
        Assert.Contains("path conflict", error.Message);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "bin")));

        fixture.WriteConfig("win-x64", new SpecFixture("b"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("flat", File.ReadAllText(Path.Combine(fixture.Root, "bin")));
    }

    [Fact]
    public void Sync_UpgradesManagedFileToDirectory()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["tool"] = "old" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["tool/data"] = "new" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.True(Directory.Exists(Path.Combine(fixture.Root, "tool")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(fixture.Root, "tool", "data")));
    }

    [Fact]
    public void Sync_RollsBackFileToDirectoryConversion()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["tool"] = "old" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["tool/data"] = "new" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));
        try
        {
            Deployer.AppliedForTests.Value = count =>
            {
                if (count == 1)
                {
                    throw new IOException("injected deployment failure");
                }
            };
            Assert.Throws<IOException>(
                () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));
        }
        finally
        {
            Deployer.AppliedForTests.Value = null;
        }

        var tool = Path.Combine(fixture.Root, "tool");
        Assert.False(Directory.Exists(tool));
        Assert.Equal("old", File.ReadAllText(tool));
        Assert.False(File.Exists(Path.Combine(fixture.Root, ".seiko", "transaction.json")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".seiko", "transaction")));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        Assert.Equal("new", File.ReadAllText(Path.Combine(tool, "data")));
    }

    [Fact]
    public void Sync_RollsBackNestedFileToDirectoryConversion()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["tool"] = "old" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["tool/sub/data"] = "new" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));
        try
        {
            Deployer.AppliedForTests.Value = count =>
            {
                if (count == 1)
                {
                    throw new IOException("injected deployment failure");
                }
            };
            Assert.Throws<IOException>(
                () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));
        }
        finally
        {
            Deployer.AppliedForTests.Value = null;
        }

        var tool = Path.Combine(fixture.Root, "tool");
        Assert.False(Directory.Exists(tool));
        Assert.Equal("old", File.ReadAllText(tool));
        Assert.False(File.Exists(Path.Combine(fixture.Root, ".seiko", "transaction.json")));
    }

    [Fact]
    public void Recover_RestoresFileAcrossNestedDirectoryConversion()
    {
        using var fixture = new Fixture();
        var root = fixture.Root;
        var stateDirectory = Path.Combine(root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var tool = Path.Combine(root, "tool");
        File.WriteAllText(tool, "old");

        var transaction = DeploymentTransaction.Begin(root, stateDirectory);
        transaction.WillChange("tool");
        transaction.WillChange("tool/sub/data");
        transaction.WriteJournal();

        File.Delete(tool);
        Directory.CreateDirectory(Path.Combine(tool, "sub"));
        File.WriteAllText(Path.Combine(tool, "sub", "data"), "new");

        DeploymentTransaction.Recover(root, stateDirectory);

        Assert.False(Directory.Exists(tool));
        Assert.Equal("old", File.ReadAllText(tool));
        Assert.False(File.Exists(Path.Combine(stateDirectory, "transaction.json")));
    }

    [Fact]
    public void Recover_KeepsNonEmptyDirectoryInsteadOfRestoringFile()
    {
        using var fixture = new Fixture();
        var root = fixture.Root;
        var stateDirectory = Path.Combine(root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var tool = Path.Combine(root, "tool");
        File.WriteAllText(tool, "old");

        var transaction = DeploymentTransaction.Begin(root, stateDirectory);
        transaction.WillChange("tool");
        transaction.WillChange("tool/sub/data");
        transaction.WriteJournal();

        File.Delete(tool);
        Directory.CreateDirectory(Path.Combine(tool, "sub"));
        File.WriteAllText(Path.Combine(tool, "sub", "data"), "new");
        File.WriteAllText(Path.Combine(tool, "keep.txt"), "keep");

        var error = Assert.Throws<SeikoException>(
            () => DeploymentTransaction.Recover(root, stateDirectory));

        Assert.Contains("non-empty directory", error.Message);
        Assert.True(Directory.Exists(tool));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(tool, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(stateDirectory, "transaction.json")));
    }

    [Fact]
    public void Recover_RestoresFileAcrossDirectoryConversion()
    {
        using var fixture = new Fixture();
        var root = fixture.Root;
        var stateDirectory = Path.Combine(root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var tool = Path.Combine(root, "tool");
        File.WriteAllText(tool, "old");

        var transaction = DeploymentTransaction.Begin(root, stateDirectory);
        transaction.WillChange("tool");
        transaction.WillChange("tool/data");
        transaction.WriteJournal();

        File.Delete(tool);
        Directory.CreateDirectory(tool);
        File.WriteAllText(Path.Combine(tool, "data"), "new");

        DeploymentTransaction.Recover(root, stateDirectory);

        Assert.False(Directory.Exists(tool));
        Assert.Equal("old", File.ReadAllText(tool));
        Assert.False(File.Exists(Path.Combine(stateDirectory, "transaction.json")));
        Assert.False(Directory.Exists(Path.Combine(stateDirectory, "transaction")));
    }

    [Fact]
    public void Sync_CleansUpCreatedDirectoriesOnFailure()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["sub/hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));

        try
        {
            Deployer.AppliedForTests.Value = count =>
            {
                if (count == 1)
                {
                    throw new IOException("injected deployment failure");
                }
            };
            Assert.Throws<IOException>(
                () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));
        }
        finally
        {
            Deployer.AppliedForTests.Value = null;
        }

        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "sub")));
        Assert.Empty(Directory.EnumerateFiles(
            fixture.Root,
            "*.seiko-tmp-*",
            SearchOption.AllDirectories));
    }

    [Fact]
    public void Recover_RollsBackAnUncommittedJournal()
    {
        using var fixture = new Fixture();
        var stateDirectory = Path.Combine(fixture.Root, ".seiko");
        var file = Path.Combine(fixture.Root, "file.txt");
        File.WriteAllText(file, "old");

        var transaction = DeploymentTransaction.Begin(fixture.Root, stateDirectory);
        transaction.WillChange("file.txt");
        transaction.WriteJournal();
        File.WriteAllText(file, "new");

        DeploymentTransaction.Recover(fixture.Root, stateDirectory);

        Assert.Equal("old", File.ReadAllText(file));
        Assert.False(File.Exists(Path.Combine(stateDirectory, "transaction.json")));
        Assert.False(Directory.Exists(Path.Combine(stateDirectory, "transaction")));
    }
}
