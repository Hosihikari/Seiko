using System.Text.Json;
using Xunit;

namespace Seiko.Tests;

public sealed class SecurityTests
{
    [Fact]
    public void Sync_RejectsMalformedArtifactDigest()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var seiPath = Path.Combine(fixture.SourceDirectory, "sei", "hello.json");
        var index = JsonSerializer.Deserialize(File.ReadAllBytes(seiPath), SeikoJsonContext.Default.SeiIndex)!;
        index.Versions[0].Artifacts[0].Source.Sha256 = "../victim";
        File.WriteAllBytes(seiPath, JsonSerializer.SerializeToUtf8Bytes(index, SeikoJsonContext.Default.SeiIndex));

        var victim = Path.Combine(fixture.Root, "victim");
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Combine(victim, "keep.txt"), "keep");

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("checksum", error.Code);
        Assert.True(File.Exists(Path.Combine(victim, "keep.txt")));
    }

    [Fact]
    public void Sync_RejectsPayloadThroughDirectorySymlink()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["escape/payload.txt"] = "evil" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));

        var outside = Path.Combine(Path.GetTempPath(), "seiko-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "escape"), outside);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                return;
            }

            var error = Assert.Throws<SeikoException>(
                () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

            Assert.Equal("deploy", error.Code);
            Assert.False(File.Exists(Path.Combine(outside, "payload.txt")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Sync_SendsTokensOnlyToTheIndexOrigin()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        using var indexServer = new TestHttpServer(fixture.SourceDirectory);
        using var artifactServer = new TestHttpServer(fixture.SourceDirectory);
        fixture.WriteHttpConfig(indexServer.BaseUrl, "win-x64", new SpecFixture("hello", "[1.0.0]"));

        var seiPath = Path.Combine(fixture.SourceDirectory, "sei", "hello.json");
        var index = JsonSerializer.Deserialize(File.ReadAllBytes(seiPath), SeikoJsonContext.Default.SeiIndex)!;
        index.Versions[0].Artifacts[0].Source.Url =
            artifactServer.BaseUrl + index.Versions[0].Artifacts[0].Source.Url;
        File.WriteAllBytes(seiPath, JsonSerializer.SerializeToUtf8Bytes(index, SeikoJsonContext.Default.SeiIndex));

        var configPath = Path.Combine(fixture.Root, "seiko.conf");
        var config = File.ReadAllText(configPath)
            .Replace(
                $"index = \"{indexServer.BaseUrl}index.json\"",
                $"index = \"{indexServer.BaseUrl}index.json\", tokenEnv = \"SEIKO_TEST_TOKEN\"");
        File.WriteAllText(configPath, config);

        Environment.SetEnvironmentVariable("SEIKO_TEST_TOKEN", "secret-token");
        try
        {
            new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEIKO_TEST_TOKEN", null);
        }

        Assert.Contains(indexServer.Authorizations, value => value == "Bearer secret-token");
        Assert.DoesNotContain(artifactServer.Authorizations, value => value is not null);
    }

    [Theory]
    [InlineData("seiko.conf")]
    [InlineData("seiko.lock.json")]
    [InlineData(".seiko/state.json")]
    [InlineData("SEIKO.CONF")]
    [InlineData("SEIKO.LOCK.JSON")]
    [InlineData(".SEIKO/state.json")]
    public void Sync_RejectsReservedControlPaths(string path)
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { [path] = "evil" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var configPath = Path.Combine(fixture.Root, "seiko.conf");
        var before = File.ReadAllText(configPath);

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("deploy", error.Code);
        Assert.Equal(before, File.ReadAllText(configPath));
    }

    [Fact]
    public void Sync_FailsWhenTheRootRegistryIsLocked()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        using var registryLock = ProcessLock.Acquire(
            Path.Combine(fixture.StoreDirectory, "roots.lock"),
            "test registry");

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("io", error.Code);
    }

    [Fact]
    public void CacheClean_FailsWhenARegisteredRootIsBusy()
    {
        using var first = new Fixture();
        using var second = new Fixture();
        var sharedStore = Path.Combine(first.Root, "shared-store");
        first.AddSei("first", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["first.txt"] = "first" } }],
        });
        first.WriteConfig("win-x64", new SpecFixture("first", "[1.0.0]"));
        second.AddSei("second", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["second.txt"] = "second" } }],
        });
        second.WriteConfig("win-x64", new SpecFixture("second", "[1.0.0]"));

        var client = new SeikoClient();
        client.Sync(first.Root, sharedStore);
        client.Sync(second.Root, sharedStore);
        using var busy = ProcessLock.Acquire(
            Path.Combine(second.Root, ".seiko", "install.lock"),
            "test root");

        var error = Assert.Throws<SeikoException>(() => client.CacheClean(first.Root, sharedStore));

        Assert.Equal("lock", error.Code);
    }

    [Fact]
    public void ProcessLock_KeepsItsFileAfterRelease()
    {
        var directory = Path.Combine(Path.GetTempPath(), "seiko-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "test.lock");
            using (var first = ProcessLock.Acquire(path, "test"))
            {
                Assert.NotNull(first);
                Assert.Throws<SeikoException>(() => ProcessLock.Acquire(path, "test"));
            }

            Assert.True(File.Exists(path));
            using var again = ProcessLock.Acquire(path, "test");
            Assert.NotNull(again);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Sync_RejectsSameSizeStoreCorruptionWithACleanDeployedCopy()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var rootFile = Path.Combine(fixture.Root, "hello.txt");
        var content = File.ReadAllBytes(rootFile);
        File.Delete(rootFile);
        File.WriteAllBytes(rootFile, content);

        var artifact = client.ReadLock(fixture.Root)!.Sei.Single().Artifact.Sha256;
        File.WriteAllText(Path.Combine(fixture.StoreDirectory, artifact, "hello.txt"), "HELLO");

        var error = Assert.Throws<SeikoException>(
            () => client.Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("checksum", error.Code);
    }

    [Fact]
    public void Deploy_InvalidModeFailsBeforeChangingFiles()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["a.txt"] = "same" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var path = Path.Combine(fixture.Root, "a.txt");
        var mode = File.GetUnixFileMode(path);

        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture
            {
                Files = { ["a.txt"] = "same" },
                Modes = new Dictionary<string, string> { ["a.txt"] = "9999" },
            }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));

        var error = Assert.Throws<SeikoException>(
            () => client.Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("deploy", error.Code);
        Assert.Equal("same", File.ReadAllText(path));
        Assert.Equal(mode, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Recover_RestoresModeOnlyChanges()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new Fixture();
        var stateDirectory = Path.Combine(fixture.Root, ".seiko");
        var path = Path.Combine(fixture.Root, "file.txt");
        File.WriteAllText(path, "content");
        var original = File.GetUnixFileMode(path);

        var transaction = DeploymentTransaction.Begin(fixture.Root, stateDirectory);
        transaction.WillChange("file.txt");
        transaction.WriteJournal();
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        DeploymentTransaction.Recover(fixture.Root, stateDirectory);

        Assert.Equal(original, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Recover_RejectsRestoringThroughDirectorySymlink()
    {
        using var fixture = new Fixture();
        var stateDirectory = Path.Combine(fixture.Root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var outside = Path.Combine(Path.GetTempPath(), "seiko-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "escape"), outside);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                return;
            }

            var journal = new DeploymentJournal
            {
                SchemaVersion = 1,
                Entries = [new DeploymentJournalEntry { Path = "escape/payload.txt" }],
            };
            File.WriteAllBytes(
                Path.Combine(stateDirectory, "transaction.json"),
                JsonSerializer.SerializeToUtf8Bytes(journal, SeikoJsonContext.Default.DeploymentJournal));

            Assert.Throws<SeikoException>(() => DeploymentTransaction.Recover(fixture.Root, stateDirectory));

            Assert.False(File.Exists(Path.Combine(outside, "payload.txt")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Recover_RejectsEscapingJournalPath()
    {
        using var fixture = new Fixture();
        var stateDirectory = Path.Combine(fixture.Root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        var journal = new DeploymentJournal
        {
            SchemaVersion = 1,
            Entries = [new DeploymentJournalEntry { Path = "../victim" }],
        };
        File.WriteAllBytes(
            Path.Combine(stateDirectory, "transaction.json"),
            JsonSerializer.SerializeToUtf8Bytes(journal, SeikoJsonContext.Default.DeploymentJournal));
        var victim = Path.Combine(fixture.Root, "victim");
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Combine(victim, "keep.txt"), "keep");

        Assert.Throws<SeikoException>(() => DeploymentTransaction.Recover(fixture.Root, stateDirectory));

        Assert.True(File.Exists(Path.Combine(victim, "keep.txt")));
    }
}
