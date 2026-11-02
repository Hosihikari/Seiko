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
    public void Rollback_RestoresFileAfterDirectoryUpgrade()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["tool"] = "old" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["tool/x"] = "new" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var result = client.Rollback(fixture.Root, fixture.StoreDirectory, toGeneration: 1);

        Assert.Equal(1, result.Generation);
        var tool = Path.Combine(fixture.Root, "tool");
        Assert.False(Directory.Exists(tool));
        Assert.Equal("old", File.ReadAllText(tool));
    }

    [Fact]
    public void Rollback_RejectsUnmanagedFilesInConvertedDirectory()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["tool"] = "old" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["tool/x"] = "new" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var keep = Path.Combine(fixture.Root, "tool", "keep.txt");
        File.WriteAllText(keep, "keep");

        var error = Assert.Throws<SeikoException>(
            () => client.Rollback(fixture.Root, fixture.StoreDirectory, toGeneration: 1));

        Assert.Equal("deploy", error.Code);
        Assert.Contains("unmanaged", error.Message);
        Assert.Equal("keep", File.ReadAllText(keep));
    }

    [Fact]
    public void Rollback_RejectsUnmanagedEmptyDirectoriesInConvertedDirectory()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["tool"] = "old" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["tool/x"] = "new" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var nested = Path.Combine(fixture.Root, "tool", "user-empty", "nested");
        Directory.CreateDirectory(nested);

        var error = Assert.Throws<SeikoException>(
            () => client.Rollback(fixture.Root, fixture.StoreDirectory, toGeneration: 1));

        Assert.Equal("deploy", error.Code);
        Assert.True(Directory.Exists(nested));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "tool", "x")));
    }

    [Fact]
    public void Sync_DowngradesDirectoryToFile()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["tool"] = "old" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["tool/x"] = "new" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));
        client.Sync(fixture.Root, fixture.StoreDirectory);

        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var tool = Path.Combine(fixture.Root, "tool");
        Assert.False(Directory.Exists(tool));
        Assert.Equal("old", File.ReadAllText(tool));
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
        transaction.WillChange("tool/sub/data", sha256: Hashing.Sha256Bytes("new"u8));
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
    public void Recover_RemovesJournaledTemporaryFiles()
    {
        using var fixture = new Fixture();
        var root = fixture.Root;
        var stateDirectory = Path.Combine(root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var tool = Path.Combine(root, "tool");
        var temporary = "x.seiko-tmp-" + new string('a', 32);
        File.WriteAllText(tool, "old");

        var transaction = DeploymentTransaction.Begin(root, stateDirectory);
        transaction.WillChange("tool");
        transaction.WillChange("tool/x");
        transaction.WillCreateTemporary("tool/" + temporary);
        transaction.WriteJournal();

        File.Delete(tool);
        Directory.CreateDirectory(tool);
        File.WriteAllText(Path.Combine(tool, temporary), "partial");

        DeploymentTransaction.Recover(root, stateDirectory);

        Assert.False(Directory.Exists(tool));
        Assert.Equal("old", File.ReadAllText(tool));
        Assert.False(File.Exists(Path.Combine(stateDirectory, "transaction.json")));
    }

    [Fact]
    public void Recover_DoesNotDeleteUnjournaledTemporaryLookalikes()
    {
        using var fixture = new Fixture();
        var root = fixture.Root;
        var stateDirectory = Path.Combine(root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var tool = Path.Combine(root, "tool");
        var temporary = "x.seiko-tmp-" + new string('a', 32);
        File.WriteAllText(tool, "old");

        var transaction = DeploymentTransaction.Begin(root, stateDirectory);
        transaction.WillChange("tool");
        transaction.WillChange("tool/x");
        transaction.WillCreateTemporary("tool/" + temporary);
        transaction.WriteJournal();

        File.Delete(tool);
        Directory.CreateDirectory(tool);
        File.WriteAllText(Path.Combine(tool, temporary), "partial");
        File.WriteAllText(Path.Combine(tool, "other.seiko-tmp-crash"), "keep");

        var error = Assert.Throws<SeikoException>(
            () => DeploymentTransaction.Recover(root, stateDirectory));

        Assert.Contains("non-empty directory", error.Message);
        Assert.True(File.Exists(Path.Combine(tool, "other.seiko-tmp-crash")));
        Assert.False(File.Exists(Path.Combine(tool, temporary)));
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
        transaction.WillChange("tool/sub/data", sha256: Hashing.Sha256Bytes("new"u8));
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
        transaction.WillChange("tool/data", sha256: Hashing.Sha256Bytes("new"u8));
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
    public void CommitFailureRollbackKeepsTheJournalUncommitted()
    {
        using var fixture = new Fixture();
        var root = fixture.Root;
        var stateDirectory = Path.Combine(root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var a = Path.Combine(root, "a.txt");
        var b = Path.Combine(root, "b.txt");
        File.WriteAllText(a, "OLD-A");
        File.WriteAllText(b, "OLD-B");

        var transaction = DeploymentTransaction.Begin(root, stateDirectory);
        transaction.WillChange("b.txt");
        transaction.WillChange("a.txt");
        transaction.WriteJournal();
        File.WriteAllText(a, "NEW-A");
        File.WriteAllText(b, "NEW-B");

        var journalPath = Path.Combine(stateDirectory, "transaction.json");
        var backupB = Path.Combine(stateDirectory, "transaction", "b.txt");
        var journalCopy = Path.Combine(stateDirectory, "journal-copy.json");
        var backupCopy = Path.Combine(stateDirectory, "b-backup-copy");
        File.Copy(journalPath, journalCopy);
        File.Copy(backupB, backupCopy);

        File.Delete(journalPath);
        Directory.CreateDirectory(journalPath);
        try
        {
            Assert.ThrowsAny<Exception>(() => transaction.Commit());
        }
        finally
        {
            Directory.Delete(journalPath);
            File.Move(journalCopy, journalPath);
        }

        // Make the rollback stop after restoring a.txt.
        File.Delete(backupB);
        var rollback = Assert.Throws<SeikoException>(
            () => transaction.RollbackAfterFailure(new IOException("commit failed")));
        Assert.Contains("rollback also failed", rollback.Message);

        var persisted = JsonSerializer.Deserialize(
            File.ReadAllBytes(journalPath),
            SeikoJsonContext.Default.DeploymentJournal)!;
        Assert.False(persisted.Committed);
        Assert.Equal("OLD-A", File.ReadAllText(a));
        Assert.Equal("NEW-B", File.ReadAllText(b));

        // Repair the missing backup and let the next recovery finish.
        File.Move(backupCopy, backupB);
        DeploymentTransaction.Recover(root, stateDirectory);

        Assert.Equal("OLD-A", File.ReadAllText(a));
        Assert.Equal("OLD-B", File.ReadAllText(b));
        Assert.False(File.Exists(journalPath));
    }

    [Fact]
    public void Recover_RejectsDeletingAnUnrelatedFile()
    {
        using var fixture = new Fixture();
        var root = fixture.Root;
        var stateDirectory = Path.Combine(root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var victim = Path.Combine(root, "victim.txt");
        File.WriteAllText(victim, "keep");
        var journal = new DeploymentJournal
        {
            Entries = [new DeploymentJournalEntry { Path = "victim.txt", Existed = false }],
        };
        File.WriteAllBytes(
            Path.Combine(stateDirectory, "transaction.json"),
            JsonSerializer.SerializeToUtf8Bytes(journal, SeikoJsonContext.Default.DeploymentJournal));

        var error = Assert.Throws<SeikoException>(
            () => DeploymentTransaction.Recover(root, stateDirectory));

        Assert.Equal("deploy", error.Code);
        Assert.Contains("not a file created", error.Message);
        Assert.Equal("keep", File.ReadAllText(victim));
    }

    [Fact]
    public void Recover_RejectsADigestWithoutAnOwnershipMarker()
    {
        using var fixture = new Fixture();
        var root = fixture.Root;
        var stateDirectory = Path.Combine(root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var victim = Path.Combine(root, "victim.txt");
        File.WriteAllText(victim, "victim");
        var journal = new DeploymentJournal
        {
            Entries =
            [
                new DeploymentJournalEntry
                {
                    Path = "victim.txt",
                    Existed = false,
                    Sha256 = Hashing.Sha256Bytes("victim"u8),
                },
            ],
        };
        File.WriteAllBytes(
            Path.Combine(stateDirectory, "transaction.json"),
            JsonSerializer.SerializeToUtf8Bytes(journal, SeikoJsonContext.Default.DeploymentJournal));

        var error = Assert.Throws<SeikoException>(
            () => DeploymentTransaction.Recover(root, stateDirectory));

        Assert.Contains("not a file created", error.Message);
        Assert.Equal("victim", File.ReadAllText(victim));
    }

    [Fact]
    public void Recover_RejectsAnUnmarkedControlFile()
    {
        using var fixture = new Fixture();
        var root = fixture.Root;
        var stateDirectory = Path.Combine(root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var lockFile = Path.Combine(root, "seiko.lock.json");
        File.WriteAllText(lockFile, "keep");
        var journal = new DeploymentJournal
        {
            Entries = [new DeploymentJournalEntry { Path = "seiko.lock.json", Existed = false }],
        };
        File.WriteAllBytes(
            Path.Combine(stateDirectory, "transaction.json"),
            JsonSerializer.SerializeToUtf8Bytes(journal, SeikoJsonContext.Default.DeploymentJournal));

        var error = Assert.Throws<SeikoException>(
            () => DeploymentTransaction.Recover(root, stateDirectory));

        Assert.Contains("not a file created", error.Message);
        Assert.Equal("keep", File.ReadAllText(lockFile));
    }

    [Fact]
    public void Recover_DeletesACreatedFileWithTheRecordedDigest()
    {
        using var fixture = new Fixture();
        var root = fixture.Root;
        var stateDirectory = Path.Combine(root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var created = Path.Combine(root, "created.txt");
        var transaction = DeploymentTransaction.Begin(root, stateDirectory);
        transaction.WillChange("created.txt", sha256: Hashing.Sha256Bytes("created"u8));
        transaction.WriteJournal();
        File.WriteAllText(created, "created");

        DeploymentTransaction.Recover(root, stateDirectory);

        Assert.False(File.Exists(created));
    }

    [Fact]
    public void Recover_DeletesAnUnreadableCreatedFileWithTheRecordedDigest()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new Fixture();
        var root = fixture.Root;
        var stateDirectory = Path.Combine(root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var created = Path.Combine(root, "created.txt");
        var transaction = DeploymentTransaction.Begin(root, stateDirectory);
        transaction.WillChange("created.txt", sha256: Hashing.Sha256Bytes("created"u8));
        transaction.WriteJournal();
        File.WriteAllText(created, "created");
        File.SetUnixFileMode(created, UnixFileMode.None);

        DeploymentTransaction.Recover(root, stateDirectory);

        Assert.False(File.Exists(created));
    }

    [Fact]
    public void Recover_RejectsDeletingAChangedUnreadableFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new Fixture();
        var root = fixture.Root;
        var stateDirectory = Path.Combine(root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var created = Path.Combine(root, "created.txt");
        var transaction = DeploymentTransaction.Begin(root, stateDirectory);
        transaction.WillChange("created.txt", sha256: Hashing.Sha256Bytes("created"u8));
        transaction.WriteJournal();
        File.WriteAllText(created, "changed");
        File.SetUnixFileMode(created, UnixFileMode.None);

        var error = Assert.Throws<SeikoException>(
            () => DeploymentTransaction.Recover(root, stateDirectory));

        Assert.Contains("not a file created", error.Message);
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(created));
        File.SetUnixFileMode(created, UnixFileMode.UserRead);
        Assert.Equal("changed", File.ReadAllText(created));
    }

    [Fact]
    public void Recover_RejectsDeletingAChangedCreatedFile()
    {
        using var fixture = new Fixture();
        var root = fixture.Root;
        var stateDirectory = Path.Combine(root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var created = Path.Combine(root, "created.txt");
        var transaction = DeploymentTransaction.Begin(root, stateDirectory);
        transaction.WillChange("created.txt", sha256: Hashing.Sha256Bytes("created"u8));
        transaction.WriteJournal();
        File.WriteAllText(created, "changed");

        var error = Assert.Throws<SeikoException>(
            () => DeploymentTransaction.Recover(root, stateDirectory));

        Assert.Contains("not a file created", error.Message);
        Assert.Equal("changed", File.ReadAllText(created));
    }

    [Fact]
    public void Recover_RejectsDeletingAnExistingGeneration()
    {
        using var fixture = new Fixture();
        var root = fixture.Root;
        var stateDirectory = Path.Combine(root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var state = new LocalState { Generation = 1, Files = [] };
        File.WriteAllBytes(
            Path.Combine(stateDirectory, "state.json"),
            JsonSerializer.SerializeToUtf8Bytes(state, SeikoJsonContext.Default.LocalState));
        var generation = Path.Combine(stateDirectory, "generations", "1");
        Directory.CreateDirectory(generation);
        File.WriteAllText(Path.Combine(generation, "keep.txt"), "keep");
        var journal = new DeploymentJournal
        {
            NewGenerationPath = ".seiko/generations/1",
            Entries = [],
        };
        File.WriteAllBytes(
            Path.Combine(stateDirectory, "transaction.json"),
            JsonSerializer.SerializeToUtf8Bytes(journal, SeikoJsonContext.Default.DeploymentJournal));

        var error = Assert.Throws<SeikoException>(
            () => DeploymentTransaction.Recover(root, stateDirectory));

        Assert.Contains("generation directory", error.Message);
        Assert.True(Directory.Exists(generation));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(generation, "keep.txt")));
    }

    [Fact]
    public void Recover_RejectsAnUnmarkedNextGeneration()
    {
        using var fixture = new Fixture();
        var root = fixture.Root;
        var stateDirectory = Path.Combine(root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var state = new LocalState { Generation = 1, Files = [] };
        File.WriteAllBytes(
            Path.Combine(stateDirectory, "state.json"),
            JsonSerializer.SerializeToUtf8Bytes(state, SeikoJsonContext.Default.LocalState));
        var generation = Path.Combine(stateDirectory, "generations", "2");
        Directory.CreateDirectory(generation);
        File.WriteAllText(Path.Combine(generation, "keep.txt"), "keep");
        var journal = new DeploymentJournal
        {
            NewGenerationPath = ".seiko/generations/2",
            Entries = [],
        };
        File.WriteAllBytes(
            Path.Combine(stateDirectory, "transaction.json"),
            JsonSerializer.SerializeToUtf8Bytes(journal, SeikoJsonContext.Default.DeploymentJournal));

        var error = Assert.Throws<SeikoException>(
            () => DeploymentTransaction.Recover(root, stateDirectory));

        Assert.Contains("generation directory", error.Message);
        Assert.True(Directory.Exists(generation));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(generation, "keep.txt")));
    }

    [Fact]
    public void Recover_RejectsAnUnmarkedTemporaryFile()
    {
        using var fixture = new Fixture();
        var root = fixture.Root;
        var stateDirectory = Path.Combine(root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var temporary = Path.Combine(root, "victim.txt.seiko-tmp-" + new string('a', 32));
        File.WriteAllText(temporary, "keep");
        var journal = new DeploymentJournal
        {
            Entries = [new DeploymentJournalEntry { Path = "victim.txt", Existed = true }],
            TemporaryPaths = ["victim.txt.seiko-tmp-" + new string('a', 32)],
        };
        File.WriteAllBytes(
            Path.Combine(stateDirectory, "transaction.json"),
            JsonSerializer.SerializeToUtf8Bytes(journal, SeikoJsonContext.Default.DeploymentJournal));

        var error = Assert.Throws<SeikoException>(
            () => DeploymentTransaction.Recover(root, stateDirectory));

        Assert.Contains("not owned", error.Message);
        Assert.Equal("keep", File.ReadAllText(temporary));
    }

    [Fact]
    public void Recover_RejectsUnrelatedTemporaryPathsBeforeDeleting()
    {
        using var fixture = new Fixture();
        var root = fixture.Root;
        var stateDirectory = Path.Combine(root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var victim = Path.Combine(root, "victim.txt");
        File.WriteAllText(victim, "keep");
        var journal = new DeploymentJournal
        {
            TemporaryPaths = ["victim.txt"],
            Entries = [],
        };
        File.WriteAllBytes(
            Path.Combine(stateDirectory, "transaction.json"),
            JsonSerializer.SerializeToUtf8Bytes(journal, SeikoJsonContext.Default.DeploymentJournal));

        var error = Assert.Throws<SeikoException>(
            () => DeploymentTransaction.Recover(root, stateDirectory));

        Assert.Equal("deploy", error.Code);
        Assert.Contains("temporary", error.Message);
        Assert.Equal("keep", File.ReadAllText(victim));
    }

    [Fact]
    public void Recover_RejectsAnInvalidGenerationPathBeforeDeleting()
    {
        using var fixture = new Fixture();
        var root = fixture.Root;
        var stateDirectory = Path.Combine(root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var victimDirectory = Path.Combine(root, "victim");
        Directory.CreateDirectory(victimDirectory);
        File.WriteAllText(Path.Combine(victimDirectory, "keep.txt"), "keep");
        var journal = new DeploymentJournal
        {
            NewGenerationPath = "victim",
            Entries = [],
        };
        File.WriteAllBytes(
            Path.Combine(stateDirectory, "transaction.json"),
            JsonSerializer.SerializeToUtf8Bytes(journal, SeikoJsonContext.Default.DeploymentJournal));

        var error = Assert.Throws<SeikoException>(
            () => DeploymentTransaction.Recover(root, stateDirectory));

        Assert.Equal("deploy", error.Code);
        Assert.Contains("generation", error.Message);
        Assert.True(Directory.Exists(victimDirectory));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(victimDirectory, "keep.txt")));
    }

    [Fact]
    public void Recover_RestoresALongFileName()
    {
        using var fixture = new Fixture();
        var root = fixture.Root;
        var stateDirectory = Path.Combine(root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var name = new string('a', 205) + ".txt";
        var file = Path.Combine(root, name);
        File.WriteAllText(file, "old");

        var transaction = DeploymentTransaction.Begin(root, stateDirectory);
        transaction.WillChange(name);
        transaction.WriteJournal();
        File.WriteAllText(file, "new");

        DeploymentTransaction.Recover(root, stateDirectory);

        Assert.Equal("old", File.ReadAllText(file));
        Assert.False(File.Exists(Path.Combine(stateDirectory, "transaction.json")));
    }

    [Fact]
    public void Recover_PreservesUnrelatedRestoreLookalikes()
    {
        using var fixture = new Fixture();
        var root = fixture.Root;
        var stateDirectory = Path.Combine(root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var file = Path.Combine(root, "payload");
        File.WriteAllText(file, "old");

        var transaction = DeploymentTransaction.Begin(root, stateDirectory);
        transaction.WillChange("payload");
        transaction.WriteJournal();
        File.WriteAllText(file, "new");
        var lookalike = Path.Combine(root, "payload.seiko-restore");
        File.WriteAllText(lookalike, "user-data");

        DeploymentTransaction.Recover(root, stateDirectory);

        Assert.Equal("old", File.ReadAllText(file));
        Assert.Equal("user-data", File.ReadAllText(lookalike));
    }

    [Fact]
    public void Recover_RestoresManagedFilesWithRestoreSuffix()
    {
        using var fixture = new Fixture();
        var root = fixture.Root;
        var stateDirectory = Path.Combine(root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var file = Path.Combine(root, "foo");
        var suffix = Path.Combine(root, "foo.seiko-restore");
        File.WriteAllText(file, "old-foo");
        File.WriteAllText(suffix, "old-suffix");

        var transaction = DeploymentTransaction.Begin(root, stateDirectory);
        transaction.WillChange("foo");
        transaction.WillChange("foo.seiko-restore");
        transaction.WriteJournal();
        File.WriteAllText(file, "new-foo");
        File.WriteAllText(suffix, "new-suffix");

        DeploymentTransaction.Recover(root, stateDirectory);

        Assert.Equal("old-foo", File.ReadAllText(file));
        Assert.Equal("old-suffix", File.ReadAllText(suffix));
        Assert.Empty(Directory.EnumerateFiles(root, "*.seiko-restore-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Recover_ReplacesAHardlinkWithoutTouchingTheSharedPayload()
    {
        using var fixture = new Fixture();
        var root = fixture.Root;
        var stateDirectory = Path.Combine(root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var payload = Path.Combine(root, "store-payload.bin");
        File.WriteAllText(payload, "new-store");
        var deployed = Path.Combine(root, "file.txt");
        File.WriteAllText(deployed, "old-root");

        var transaction = DeploymentTransaction.Begin(root, stateDirectory);
        transaction.WillChange("file.txt");
        transaction.WriteJournal();

        File.Delete(deployed);
        Assert.True(FileLink.TryHardLink(payload, deployed));

        DeploymentTransaction.Recover(root, stateDirectory);

        Assert.Equal("old-root", File.ReadAllText(deployed));
        Assert.Equal("new-store", File.ReadAllText(payload));
        Assert.False(File.Exists(Path.Combine(stateDirectory, "transaction.json")));
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
