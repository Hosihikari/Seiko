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
    public void Sync_DoesNotSendTokensOverHttp()
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

        var host = new Uri(indexServer.BaseUrl).Host;
        var tokenVariable = HttpSource.TokenEnvironmentName(host);
        Environment.SetEnvironmentVariable(tokenVariable, "secret-token");
        try
        {
            new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenVariable, null);
        }

        Assert.DoesNotContain(indexServer.Authorizations, value => value is not null);
        Assert.DoesNotContain(artifactServer.Authorizations, value => value is not null);
    }

    [Fact]
    public void HttpSource_TokenEnvironmentNameDoesNotCollide()
    {
        Assert.Equal(
            "SEIKO_TOKEN_PACKAGES_2EEXAMPLE_2ECOM",
            HttpSource.TokenEnvironmentName("Packages.Example.com"));
        Assert.NotEqual(
            HttpSource.TokenEnvironmentName("packages.example.com"),
            HttpSource.TokenEnvironmentName("packages-example.com"));
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
    public void ArchiveLimits_EnforceHardCaps()
    {
        ArchiveLimits.EnsureEntryCount(ArchiveLimits.MaxEntries);
        ArchiveLimits.EnsureEntrySize(ArchiveLimits.MaxEntryBytes);
        ArchiveLimits.EnsureTotal(ArchiveLimits.MaxTotalBytes);

        Assert.Throws<SeikoException>(
            () => ArchiveLimits.EnsureEntryCount(ArchiveLimits.MaxEntries + 1));
        Assert.Throws<SeikoException>(
            () => ArchiveLimits.EnsureEntrySize(ArchiveLimits.MaxEntryBytes + 1));
        Assert.Throws<SeikoException>(
            () => ArchiveLimits.EnsureTotal(ArchiveLimits.MaxTotalBytes + 1));
    }

    [Fact]
    public void Urls_RedactDropsCredentialsAndQuery()
    {
        var redacted = Urls.Redact(new Uri("https://user:pass@example.com/path?token=secret#fragment"));

        Assert.Equal("https://example.com/path", redacted);
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
    public void CacheClean_HandlesSymlinkedRootAlias()
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

        var link = Path.Combine(Path.GetTempPath(), "seiko-tests", Guid.NewGuid().ToString("N"));
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, fixture.Root);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                return;
            }

            client.Sync(link, fixture.StoreDirectory);

            var result = client.CacheClean(fixture.Root, fixture.StoreDirectory);

            Assert.Equal(0, result.Removed);
            Assert.Equal(1, client.CacheStatus(fixture.StoreDirectory).Artifacts);
        }
        finally
        {
            if (Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is not null)
            {
                Directory.Delete(link);
            }
        }
    }

    [Fact]
    public void RootRegistry_ResolveNormalizesCaseOnCaseInsensitiveVolumes()
    {
        var name = "CaseRoot-" + Guid.NewGuid().ToString("N");
        var actual = Path.Combine(Path.GetTempPath(), "seiko-tests", name);
        Directory.CreateDirectory(actual);
        try
        {
            var variant = Path.Combine(Path.GetTempPath(), "seiko-tests", name.ToLowerInvariant());
            if (!Directory.Exists(variant))
            {
                return;
            }

            Assert.Equal(RootRegistry.Resolve(actual), RootRegistry.Resolve(variant));
        }
        finally
        {
            Directory.Delete(actual, recursive: true);
        }
    }

    [Fact]
    public void Deploy_RejectsCaseOnlyPathConflicts()
    {
        using var first = new Fixture();
        first.AddSei("a", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["A.txt"] = "A" } }],
        });
        first.WriteConfig("win-x64", new SpecFixture("a", "[1.0.0]"));
        using var second = new Fixture();
        second.AddSei("b", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["a.txt"] = "a" } }],
        });
        second.WriteConfig("win-x64", new SpecFixture("b", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(first.Root, first.StoreDirectory);
        client.Sync(second.Root, second.StoreDirectory);
        var storedA = new Store(first.StoreDirectory).TryGet(
            client.ReadLock(first.Root)!.Sei.Single().Artifact.Sha256)!;
        var storedB = new Store(second.StoreDirectory).TryGet(
            client.ReadLock(second.Root)!.Sei.Single().Artifact.Sha256)!;
        var root = Path.Combine(Path.GetTempPath(), "seiko-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var error = Assert.Throws<SeikoException>(() => Deployer.Deploy(
                root,
                [
                    new DeployedSei("a", "1.0.0", storedA.Directory, storedA.Manifest, storedA.Digests),
                    new DeployedSei("b", "1.0.0", storedB.Directory, storedB.Manifest, storedB.Digests),
                ],
                Path.Combine(root, ".seiko"),
                new SeikoLock { Target = new PlatformInfo { Rid = "win-x64" } }));

            Assert.Equal("deploy", error.Code);
            Assert.Contains("path conflict", error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Deploy_RejectsUnicodeEquivalentPathConflicts()
    {
        using var first = new Fixture();
        first.AddSei("a", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["café.txt"] = "NFC" } }],
        });
        first.WriteConfig("win-x64", new SpecFixture("a", "[1.0.0]"));
        using var second = new Fixture();
        second.AddSei("b", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["café.txt"] = "NFD" } }],
        });
        second.WriteConfig("win-x64", new SpecFixture("b", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(first.Root, first.StoreDirectory);
        client.Sync(second.Root, second.StoreDirectory);
        var storedA = new Store(first.StoreDirectory).TryGet(
            client.ReadLock(first.Root)!.Sei.Single().Artifact.Sha256)!;
        var storedB = new Store(second.StoreDirectory).TryGet(
            client.ReadLock(second.Root)!.Sei.Single().Artifact.Sha256)!;
        var root = Path.Combine(Path.GetTempPath(), "seiko-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var error = Assert.Throws<SeikoException>(() => Deployer.Deploy(
                root,
                [
                    new DeployedSei("a", "1.0.0", storedA.Directory, storedA.Manifest, storedA.Digests),
                    new DeployedSei("b", "1.0.0", storedB.Directory, storedB.Manifest, storedB.Digests),
                ],
                Path.Combine(root, ".seiko"),
                new SeikoLock { Target = new PlatformInfo { Rid = "win-x64" } }));

            Assert.Equal("deploy", error.Code);
            Assert.Contains("path conflict", error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CacheClean_DeduplicatesUnicodeRootAliases()
    {
        var id = Guid.NewGuid().ToString("N");
        var actual = Path.Combine(Path.GetTempPath(), "seiko-tests", "café-" + id);
        var variant = Path.Combine(Path.GetTempPath(), "seiko-tests", "café-" + id);
        var store = Path.Combine(Path.GetTempPath(), "seiko-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(actual);
        Directory.CreateDirectory(store);
        try
        {
            if (!Directory.Exists(variant))
            {
                return;
            }

            Assert.Equal(RootRegistry.Resolve(actual), RootRegistry.Resolve(variant));

            RootRegistry.Register(store, actual);
            RootRegistry.Register(store, variant);

            var error = Record.Exception(
                () => new SeikoClient().CacheClean(actual, store));

            Assert.True(error is null
                || !error.Message.Contains("in use by another process", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(actual, recursive: true);
            Directory.Delete(store, recursive: true);
        }
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
    public void Sync_RepairsSameSizeStoreCorruptionWithACleanDeployedCopy()
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
        var storeFile = Path.Combine(fixture.StoreDirectory, artifact, "hello.txt");
        File.WriteAllText(storeFile, "HELLO");

        client.Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("hello", File.ReadAllText(storeFile));
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
    public void Recover_RejectsTemporaryPathThroughSymlink()
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

            var canary = Path.Combine(outside, "canary.tmp");
            File.WriteAllText(canary, "canary");
            var journal = new DeploymentJournal
            {
                SchemaVersion = 1,
                TemporaryPaths = ["escape/canary.tmp"],
            };
            File.WriteAllBytes(
                Path.Combine(stateDirectory, "transaction.json"),
                JsonSerializer.SerializeToUtf8Bytes(journal, SeikoJsonContext.Default.DeploymentJournal));

            Assert.Throws<SeikoException>(() => DeploymentTransaction.Recover(fixture.Root, stateDirectory));

            Assert.Equal("canary", File.ReadAllText(canary));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Sync_RejectsTemporaryPathThroughSymlinkBeforeReadingConfig()
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

            var canary = Path.Combine(outside, "canary.tmp");
            File.WriteAllText(canary, "canary");
            var journal = new DeploymentJournal
            {
                SchemaVersion = 1,
                TemporaryPaths = ["escape/canary.tmp"],
            };
            File.WriteAllBytes(
                Path.Combine(stateDirectory, "transaction.json"),
                JsonSerializer.SerializeToUtf8Bytes(journal, SeikoJsonContext.Default.DeploymentJournal));

            Assert.Throws<SeikoException>(
                () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

            Assert.Equal("canary", File.ReadAllText(canary));
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
