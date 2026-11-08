using Xunit;

namespace Seiko.Tests;

public sealed class OriginTests
{
    [Fact]
    public void Add_ByPathInstallsAndRecordsTheDeclaration()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var path = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "hi");

        new SeikoClient().Add(fixture.Root, Rel(fixture, path), storeRoot: fixture.StoreDirectory);

        Assert.Equal("hi", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
        var config = ConfigLoader.Load(fixture.Root);
        var origin = Assert.Single(config.Origin);
        Assert.Equal("./dist/hello.sei".TrimStart('.').TrimStart('/').Replace('/', '/'), Normalize(origin.Paths[0]));
        Assert.Equal("hello", Assert.Single(config.Sei).Name);

        var locked = Assert.Single(new SeikoClient().ReadLock(fixture.Root)!.Sei);
        Assert.Equal("origin", locked.Source);
        Assert.Equal("dist/hello.sei", locked.OriginPath?.Replace('\\', '/'));
    }

    [Fact]
    public void Add_ByPathUsesTheFileNameAndClearsANameDeclaration()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));
        var path = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "hi");

        new SeikoClient().Add(fixture.Root, Rel(fixture, path), storeRoot: fixture.StoreDirectory);

        var spec = Assert.Single(ConfigLoader.Load(fixture.Root).Sei);
        Assert.Null(spec.Version);
        Assert.Single(ConfigLoader.Load(fixture.Root).Origin);
    }

    [Fact]
    public void Add_ByNameClearsTheOrigin()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteConfig("win-x64");
        var path = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "hi");
        var client = new SeikoClient();
        client.Add(fixture.Root, Rel(fixture, path), storeRoot: fixture.StoreDirectory);

        client.Add(fixture.Root, "hello", storeRoot: fixture.StoreDirectory);

        Assert.Empty(ConfigLoader.Load(fixture.Root).Origin);
        Assert.NotEqual("origin", Assert.Single(client.ReadLock(fixture.Root)!.Sei).Source);
    }

    [Fact]
    public void Add_ReportsABareFileNameThatIsAlsoASourcePackage()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteConfig("win-x64");
        // A bare token is only a path because a file of that name exists in
        // the working directory (ADR-0044), so the test puts one there.
        var local = Path.Combine(Directory.GetCurrentDirectory(), "hello.sei");
        File.Copy(Path.Combine(fixture.Root, fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "hi")), local, overwrite: true);
        try
        {
            var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
                fixture.Root,
                "hello.sei",
                storeRoot: fixture.StoreDirectory));

            Assert.Equal("usage", error.Code);
            Assert.Contains("./hello.sei", error.Message);
        }
        finally
        {
            File.Delete(local);
        }
    }

    [Fact]
    public void Add_RejectsAnAbsoluteConfigPath()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var absolute = Path.Combine(fixture.Root, fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "hi"));
        var path = Path.Combine(fixture.Root, "seiko.conf");
        File.WriteAllText(
            path,
            File.ReadAllText(path)
            + $"origin = [ {{ name = \"hello\", paths = [\"{absolute.Replace('\\', '/')}\"], selectors = [ {{ rid = \"*\" }} ] }} ]\n");

        var error = Assert.Throws<SeikoException>(() => ConfigLoader.Load(fixture.Root));

        Assert.Equal("config", error.Code);
        Assert.Contains("relative", error.Message);
    }

    [Fact]
    public void Add_RejectsARangeOnAPath()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "hi");

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Add(fixture.Root, "./dist/hello.sei@1.0.0", storeRoot: fixture.StoreDirectory));

        Assert.Equal("usage", error.Code);
    }

    [Fact]
    public void Add_RejectsAMissingPath()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root,
            "./dist/nope.sei",
            storeRoot: fixture.StoreDirectory));

        Assert.Equal("usage", error.Code);
        Assert.Contains("not found", error.Message);
    }

    [Fact]
    public void Add_RejectsAFileNameThatContradictsTheManifest()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var path = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "hi");
        File.Move(
            Path.Combine(fixture.Root, path),
            Path.Combine(fixture.Root, "dist", "other.sei"));

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root,
            Rel(fixture, "dist/other.sei"),
            storeRoot: fixture.StoreDirectory));

        Assert.Equal("usage", error.Code);
        Assert.Contains("file name", error.Message);
    }

    [Fact]
    public void Add_RejectsExternalDeclarations()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var path = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "hi");
        var full = Path.Combine(fixture.Root, path);
        using (var archive = System.IO.Compression.ZipFile.Open(full, System.IO.Compression.ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("sei.json")!;
            string text;
            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream))
            {
                text = reader.ReadToEnd();
            }

            entry.Delete();
            var replacement = archive.CreateEntry("sei.json");
            using var output = replacement.Open();
            using var writer = new StreamWriter(output);
            var node = System.Text.Json.Nodes.JsonNode.Parse(text)!;
            node["schemaVersion"] = 3;
            node["artifact"]!["externalFiles"] = System.Text.Json.Nodes.JsonNode.Parse(
                $$"""[{"path":"e.bin","url":"https://example.invalid/e.bin","sha256":"{{new string('a', 64)}}","size":1}]""");
            node["artifact"]!.AsObject().Remove("externalArchives");
            writer.Write(node.ToJsonString());
        }

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root,
            Rel(fixture, path),
            storeRoot: fixture.StoreDirectory));

        Assert.Equal("source", error.Code);
        Assert.Contains("external", error.Message);
    }

    [Fact]
    public void Remove_DropsTheOriginEntry()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var client = new SeikoClient();
        client.Add(
            fixture.Root,
            Rel(fixture, fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one")),
            storeRoot: fixture.StoreDirectory);

        client.Remove(fixture.Root, "hello", storeRoot: fixture.StoreDirectory);

        var config = ConfigLoader.Load(fixture.Root);
        Assert.Empty(config.Origin);
        Assert.Empty(config.Sei);
    }

    [Fact]
    public void Frozen_FallsBackToTheLockedSnapshot()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var client = new SeikoClient();
        var path = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one");
        client.Add(fixture.Root, Rel(fixture, path), storeRoot: fixture.StoreDirectory);

        // Rebuilt under the same version: the file is gone, so a replay must
        // still serve the granted bytes from the snapshot.
        fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "two");
        File.Delete(Path.Combine(fixture.Root, path));
        client.Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen);

        Assert.Equal("one", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Frozen_ReportsWhenNeitherFileNorSnapshotExists()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var client = new SeikoClient();
        var path = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one");
        client.Add(fixture.Root, Rel(fixture, path), storeRoot: fixture.StoreDirectory);
        File.Delete(Path.Combine(fixture.Root, path));
        foreach (var snapshot in Directory.GetFiles(Path.Combine(fixture.StoreDirectory, "origin")))
        {
            File.Delete(snapshot);
        }

        var error = Assert.Throws<SeikoException>(
            () => client.Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen));

        Assert.Equal("checksum", error.Code);
        Assert.Contains("no snapshot", error.Message);
    }

    [Fact]
    public void Sync_FollowsANewVersion()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var client = new SeikoClient();
        client.Add(
            fixture.Root,
            Rel(fixture, fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one")),
            storeRoot: fixture.StoreDirectory);

        client.Add(
            fixture.Root,
            Rel(fixture, fixture.WriteOrigin("hello", "1.0.1", "hello.txt", "two")),
            storeRoot: fixture.StoreDirectory);
        client.Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("1.0.1", Assert.Single(client.ReadLock(fixture.Root)!.Sei).Version);
        Assert.Equal("two", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Sync_RejectsRebuiltBytesUnderTheSameVersion()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var client = new SeikoClient();
        client.Add(
            fixture.Root,
            Rel(fixture, fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one")),
            storeRoot: fixture.StoreDirectory);

        fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "two");
        var error = Assert.Throws<SeikoException>(
            () => client.Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("checksum", error.Code);
        Assert.NotNull(error.HintKey);
        Assert.Equal("1.0.0", Assert.Single(client.ReadLock(fixture.Root)!.Sei).Version);
    }

    [Fact]
    public void Sync_ReplaysFromTheSnapshotWhenTheFileIsGone()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var client = new SeikoClient();
        var path = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one");
        client.Add(fixture.Root, Rel(fixture, path), storeRoot: fixture.StoreDirectory);
        File.Delete(Path.Combine(fixture.Root, path));
        File.Delete(Path.Combine(fixture.Root, "hello.txt"));
        client.Sync(fixture.Root, fixture.StoreDirectory, force: true);

        Assert.Equal("one", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Add_OfflineIsAllowedForAPath()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var path = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "hi");

        new SeikoClient().Add(
            fixture.Root,
            Rel(fixture, path),
            storeRoot: fixture.StoreDirectory,
            offline: true);

        Assert.Equal("hi", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Config_RejectsAReservedOriginSourceId()
    {
        using var fixture = new Fixture();
        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            "schemaVersion = 2\n"
            + "sources = [ { id = \"origin\", index = \"https://example.invalid/index.json\" } ]\n"
            + "sei = []\n");

        var error = Assert.Throws<SeikoException>(() => ConfigLoader.Load(fixture.Root));

        Assert.Equal("config", error.Code);
    }

    [Fact]
    public void CacheClean_KeepsReferencedSnapshotsAndDropsOrphans()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var client = new SeikoClient();
        client.Add(
            fixture.Root,
            Rel(fixture, fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one")),
            storeRoot: fixture.StoreDirectory);
        var folder = Path.Combine(fixture.StoreDirectory, "origin");
        var referenced = Assert.Single(Directory.GetFiles(folder));
        var orphan = Path.Combine(folder, "hello-" + new string('c', 64) + ".sei");
        File.WriteAllText(orphan, "x");

        client.CacheClean(fixture.Root, fixture.StoreDirectory);

        Assert.True(File.Exists(referenced));
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public void Capture_RepairsACorruptedSnapshot()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var client = new SeikoClient();
        var path = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one");
        client.Add(fixture.Root, Rel(fixture, path), storeRoot: fixture.StoreDirectory);
        var snapshot = Assert.Single(
            Directory.GetFiles(Path.Combine(fixture.StoreDirectory, "origin")));
        var expected = Hashing.Sha256File(snapshot);
        File.WriteAllText(snapshot, "tampered");

        client.Add(fixture.Root, Rel(fixture, path), storeRoot: fixture.StoreDirectory);

        Assert.Equal(expected, Hashing.Sha256File(snapshot));
    }

    [Fact]
    public void SelectorPrerequisiteMustHold()
    {
        using var fixture = new Fixture();
        fixture.AddSei("lib", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteConfig("win-x64");
        fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one");
        WriteOriginConfig(
            fixture,
            "hello",
            "dist/hello.sei",
            "selectors = [ { rid = \"*\", prerequisites = [ { name = \"lib\", version = \"[1.0.0]\" } ] } ]",
            "lib");

        // lib is direct, so its presence proves nothing; the assertion is that
        // the selector's condition reached the locked artifact.
        new SeikoClient().Add(
            fixture.Root,
            Rel(fixture, "dist/hello.sei"),
            storeRoot: fixture.StoreDirectory);

        var sei = Assert.Single(new SeikoClient().ReadLock(fixture.Root)!.Sei, item => item.Name == "hello");
        Assert.Contains(sei.Artifact.Prerequisites, relation => relation.Name == "lib");
    }

    [Fact]
    public void SelectorRangeIsKeptWhenTheManifestAlsoNamesThePrerequisite()
    {
        using var fixture = new Fixture();
        fixture.AddSei("lib", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteConfig("win-x64");
        // The manifest carries the same name at the artifact level, so the
        // merge path under test is the one that runs.
        fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one", artifactPrerequisites: [("lib", "[1.0.0]")]);
        WriteOriginConfig(
            fixture,
            "hello",
            "dist/hello.sei",
            "selectors = [ { rid = \"*\", prerequisites = [ { name = \"lib\", version = \"[2.0.0]\" } ] } ]",
            "lib");

        // lib 1.0.0 satisfies the manifest range but not the selector's.
        // Keeping only the manifest range would accept it, so this fails only
        // if both ranges are enforced.
        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root,
            Rel(fixture, "dist/hello.sei"),
            storeRoot: fixture.StoreDirectory));

        Assert.Equal("resolve", error.Code);
    }

    [Fact]
    public void Sync_KeepsOriginPathWhenTheFileIsGone()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var client = new SeikoClient();
        var path = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one");
        client.Add(fixture.Root, Rel(fixture, path), storeRoot: fixture.StoreDirectory);
        File.Delete(Path.Combine(fixture.Root, path));

        client.Sync(fixture.Root, fixture.StoreDirectory, force: true);

        Assert.Equal("dist/hello.sei", Assert.Single(client.ReadLock(fixture.Root)!.Sei).OriginPath);
    }

    [Fact]
    public void Sync_CapturesTheNewVersionWithoutAnAdd()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var client = new SeikoClient();
        client.Add(
            fixture.Root,
            Rel(fixture, fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one")),
            storeRoot: fixture.StoreDirectory);

        // A new version picked up by a plain sync must be snapshotted too.
        fixture.WriteOrigin("hello", "1.0.1", "hello.txt", "two");
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var folder = Path.Combine(fixture.StoreDirectory, "origin");
        Assert.Equal(2, Directory.GetFiles(folder, "*.sei").Length);
    }

    [Fact]
    public void Add_OfflineReportsEveryMissingDependency()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("a", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddSei("b", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        var origin = fixture.WriteOrigin(
            "hello",
            "1.0.0",
            "hello.txt",
            "one",
            dependencies: [("a", null), ("b", null)]);

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root,
            Rel(fixture, origin),
            storeRoot: fixture.StoreDirectory,
            offline: true));

        Assert.Equal("source", error.Code);
        Assert.Contains("sei/a.json", error.Message);
    }

    [Fact]
    public void Lock_RejectsAnAbsoluteOriginPath()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var path = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one");
        new SeikoClient().Add(fixture.Root, Rel(fixture, path), storeRoot: fixture.StoreDirectory);
        var lockPath = Path.Combine(fixture.Root, "seiko.lock.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(lockPath))!;
        node["sei"]![0]!["originPath"] = Path.Combine(fixture.Root, path).Replace('\\', '/');
        File.WriteAllText(lockPath, node.ToJsonString());

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().ReadLock(fixture.Root));

        Assert.Equal("lock", error.Code);
        Assert.Contains("relative", error.Message);
    }

    [Fact]
    public void DryRun_DoesNotWriteASnapshot()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var path = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one");

        new SeikoClient().Add(fixture.Root, Rel(fixture, path), storeRoot: fixture.StoreDirectory, dryRun: true);

        var folder = Path.Combine(fixture.StoreDirectory, "origin");
        Assert.False(Directory.Exists(folder) && Directory.GetFiles(folder).Length > 0);
    }

    [Fact]
    public void Frozen_UsesTheLockedPathWithSeveralSelectors()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "win", fileName: "hello-win.sei");
        fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "linux", fileName: "hello-linux.sei");
        WriteOriginConfig(
            fixture,
            "hello",
            "\"dist/hello-win.sei\", \"dist/hello-linux.sei\"",
            "selectors = [ { rid = \"win-x64\" }, { rid = \"linux-x64\" } ]");

        // Pin the linux file by hand, the way a moved declaration would look.
        new SeikoClient().Add(fixture.Root, Rel(fixture, "dist/hello-win.sei"), storeRoot: fixture.StoreDirectory);
        var client = new SeikoClient();
        var lockPath = Path.Combine(fixture.Root, "seiko.lock.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(lockPath))!;
        node["sei"]![0]!["originPath"] = "dist/hello-linux.sei";
        node["sei"]![0]!["artifact"]!["sha256"] =
            Hashing.Sha256File(Path.Combine(fixture.Root, "dist/hello-linux.sei"));
        File.WriteAllText(lockPath, node.ToJsonString());

        client.Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen);

        Assert.Equal("linux", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void IndexOriginPathIsNotTreatedAsALocalFile()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "indexed" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));

        // A plain indexed artifact that also mirrors the optional field.
        var indexPath = Path.Combine(fixture.SourceDirectory, "sei", "hello.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(indexPath))!;

        // The path is outside the install root, so a fixed cwd-relative read
        // (the old code) would miss it and a root-relative one would find it;
        // either way it must not be captured or compared.
        var elsewhere = Path.Combine(
            Path.GetTempPath(),
            "seiko-origin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(elsewhere);
        File.WriteAllBytes(Path.Combine(elsewhere, "elsewhere.sei"), [1, 2, 3]);
        node["versions"]![0]!["artifacts"]![0]!["originPath"] =
            Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.Combine(elsewhere, "elsewhere.sei"))
                .Replace('\\', '/');
        File.WriteAllText(indexPath, node.ToJsonString());

        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Null(Assert.Single(client.ReadLock(fixture.Root)!.Sei).OriginPath);
        Assert.False(Directory.Exists(Path.Combine(fixture.StoreDirectory, "origin")));
        Assert.Equal("indexed", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Add_OfflineReportsArtifactLevelDependencies()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("a", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddSei("b", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        // Only the artifact carries them, so reading the package level alone
        // would report nothing.
        var origin = fixture.WriteOrigin(
            "hello",
            "1.0.0",
            "hello.txt",
            "one",
            dependencies: [("a", null), ("b", null)],
            dependencyLevel: "artifact");

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root,
            Rel(fixture, origin),
            storeRoot: fixture.StoreDirectory,
            offline: true));

        // Either wording is correct: the pre-check may or may not get far
        // enough to list, but the missing names are what matters.
        // Both names must be collected in the same pass, and named as such.
        Assert.Equal("source", error.Code);
        Assert.Contains("sei/a.json", error.Message);
        Assert.Contains("sei/b.json", error.Message);
    }

    [Fact]
    public void Add_OfflineReadsDependenciesFromTheSnapshot()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("a", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        var origin = fixture.WriteOrigin(
            "hello",
            "1.0.0",
            "hello.txt",
            "one",
            dependencies: [("a", null)]);
        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        File.Delete(Path.Combine(fixture.Root, origin));
        Directory.Delete(Path.Combine(fixture.StoreDirectory, ".metadata"), recursive: true);

        // The dependency is read from the snapshot, so it is still reported
        // rather than surfacing as the first parse failure.
        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root,
            Rel(fixture, origin),
            storeRoot: fixture.StoreDirectory,
            offline: true));

        Assert.Equal("source", error.Code);
        Assert.Contains("sei/a.json", error.Message);
    }

    [Fact]
    public void Rollback_RefetchesTheOlderGenerationFromItsOwnSnapshot()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var client = new SeikoClient();
        var path = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one");
        client.Add(fixture.Root, Rel(fixture, path), storeRoot: fixture.StoreDirectory);

        // Second generation with a different version; both snapshots exist.
        path = fixture.WriteOrigin("hello", "1.0.1", "hello.txt", "two");
        client.Add(fixture.Root, Rel(fixture, path), storeRoot: fixture.StoreDirectory);

        // Remove the original and every unpacked tree, including the target
        // generation's. The current lock only names 1.0.1, so the cache is
        // cleared wholesale: otherwise rollback would never refetch and the
        // snapshot binding would go untested.
        File.Delete(Path.Combine(fixture.Root, path));
        foreach (var directory in Directory.GetDirectories(fixture.StoreDirectory))
        {
            if (Path.GetFileName(directory) is "." or "origin" || Path.GetFileName(directory) is ".locks" or ".metadata" or ".partial")
            {
                continue;
            }

            Directory.Delete(directory, recursive: true);
        }

        var meta = Path.Combine(fixture.StoreDirectory, ".meta");
        if (Directory.Exists(meta))
        {
            foreach (var file in Directory.GetFiles(meta))
            {
                File.Delete(file);
            }
        }

        client.Rollback(fixture.Root, fixture.StoreDirectory, toGeneration: 1);

        Assert.Equal("one", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
        // The newer snapshot must still be there for the generation it belongs to.
        Assert.Equal(2, Directory.GetFiles(Path.Combine(fixture.StoreDirectory, "origin")).Length);
    }

    [Fact]
    public void Frozen_PicksTheLockedSelectorConditionWithTwoMatchingRids()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "lib",
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] },
            new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteConfig("win-x64");
        fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "from-a", fileName: "hello-a.sei");
        fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "from-b", fileName: "hello-b.sei");
        WriteOriginConfig(
            fixture,
            "hello",
            "\"dist/hello-a.sei\", \"dist/hello-b.sei\"",
            "selectors = [ { rid = \"*\", prerequisites = [ { name = \"lib\", version = \"[1.0.0]\" } ] }, "
            + "{ rid = \"*\", prerequisites = [ { name = \"lib\", version = \"[2.0.0]\" } ] } ]",
            "lib@[2.0.0]");

        // Both selectors match the rid; only the second is satisfiable, so the
        // old "first selector" pairing would pick the wrong condition.
        var client = new SeikoClient();
        client.Add(fixture.Root, Rel(fixture, "dist/hello-b.sei"), storeRoot: fixture.StoreDirectory);
        var locked = Assert.Single(client.ReadLock(fixture.Root)!.Sei, item => item.Name == "hello");
        Assert.Equal("dist/hello-b.sei", locked.OriginPath);

        client.Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen);

        // The deployed bytes prove which file the replay actually used.
        Assert.Equal("from-b", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Add_FindsASnapshotForASuffixedFileNameOnTheDefaultStore()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var client = new SeikoClient();
        using var defaultStore = Fixture.UseDefaultStore(Path.Combine(fixture.Root, "default-store"));
        var path = fixture.WriteOrigin("hello-default", "1.0.0", "hello.txt", "one", fileName: "hello-default-1.0.0.sei");
        // No --store on either call: the default store must hold the snapshot,
        // and it is named after the sei rather than the file.
        client.Add(fixture.Root, Rel(fixture, path));
        File.Delete(Path.Combine(fixture.Root, path));

        client.Add(fixture.Root, Rel(fixture, path));

        Assert.Equal("dist/hello-default-1.0.0.sei", Assert.Single(ConfigLoader.Load(fixture.Root).Origin).Paths[0]);
    }

    [Fact]
    public void Add_UsesTheDeclaredNameWhenTheStemAlsoFitsAnother()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var client = new SeikoClient();
        var tools = fixture.WriteOrigin("hello-tools", "1.0.0", "tools.txt", "tools", fileName: "hello-tools-win.sei");
        client.Add(fixture.Root, Rel(fixture, tools), storeRoot: fixture.StoreDirectory);
        // `hello` is also a prefix of this stem; the declaration decides, not
        // the length of the name nor the age of the snapshot.
        fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "base", fileName: "hello.sei");
        client.Add(fixture.Root, Rel(fixture, "dist/hello.sei"), storeRoot: fixture.StoreDirectory);
        File.Delete(Path.Combine(fixture.Root, tools));

        client.Add(fixture.Root, Rel(fixture, tools), storeRoot: fixture.StoreDirectory);

        Assert.Contains(
            ConfigLoader.Load(fixture.Root).Origin,
            origin => string.Equals(origin.Name, "hello-tools", StringComparison.Ordinal));
        Assert.Equal("tools", File.ReadAllText(Path.Combine(fixture.Root, "tools.txt")));
    }

    [Fact]
    public void Frozen_RejectsARewrittenFileWithAWarmCache()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var client = new SeikoClient();
        var path = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one");
        var store = fixture.StoreDirectory;
        client.Add(fixture.Root, Rel(fixture, path), storeRoot: store);

        // Same version, same relations, different payload. The unpacked tree
        // stays, so only the file digest check can notice.
        var locked = Assert.Single(client.ReadLock(fixture.Root)!.Sei).Artifact.Sha256;
        Assert.True(Directory.Exists(Path.Combine(store, locked)));
        fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "two");

        var frozen = Assert.Throws<SeikoException>(
            () => client.Sync(fixture.Root, store, mode: SyncMode.Frozen));
        var preview = Assert.Throws<SeikoException>(
            () => client.Sync(fixture.Root, store, mode: SyncMode.Frozen, dryRun: true));

        Assert.Equal("checksum", frozen.Code);
        Assert.Equal("checksum", preview.Code);
    }

    [Fact]
    public void CacheVerify_ReportsADamagedSnapshot()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        new SeikoClient().Add(
            fixture.Root,
            Rel(fixture, fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one")),
            storeRoot: fixture.StoreDirectory);
        var snapshot = Assert.Single(Directory.GetFiles(Path.Combine(fixture.StoreDirectory, "origin")));
        File.WriteAllText(snapshot, "tampered");

        var findings = new SeikoClient().CacheVerify(fixture.StoreDirectory);

        Assert.Contains(findings, finding => finding.Code == "cache-origin-corrupt");
    }

    [Fact]
    public void OfflinePrecheck_FollowsTheSelectedSelectorRange()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("lib", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddSei("other", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("lib", "[1.0.0]"));
        // The declaration keeps its real range; WriteOriginConfig appends a
        // second sei block, so lib is pinned to 1.0.0 here.
        fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one", fileName: "hello-a.sei");
        // The second file needs lib@2 and pulls a package only it names; the
        // declaration pins lib@1, so that dependency must not be required.
        fixture.WriteOrigin(
            "hello",
            "1.0.0",
            "hello.txt",
            "two",
            fileName: "hello-b.sei",
            dependencies: [("other", null)],
            dependencyLevel: "artifact");
        WriteOriginConfig(
            fixture,
            "hello",
            "\"dist/hello-a.sei\", \"dist/hello-b.sei\"",
            "selectors = [ { rid = \"*\", prerequisites = [ { name = \"lib\", version = \"[1.0.0]\" } ] }, "
            + "{ rid = \"*\", prerequisites = [ { name = \"lib\", version = \"[2.0.0]\" } ] } ]",
            "lib@[1.0.0]");

        // hello-b's selector needs lib@2 while the declaration pins lib@1, so
        // the resolver will use hello-a; `other` is only named by hello-b's
        // artifact and must not become a cache requirement. Without the range
        // check the pre-check would demand it and this would fail offline.
        new SeikoClient().Add(fixture.Root, Rel(fixture, "dist/hello-a.sei"), storeRoot: fixture.StoreDirectory);
        new SeikoClient().Add(
            fixture.Root,
            Rel(fixture, "dist/hello-b.sei"),
            storeRoot: fixture.StoreDirectory,
            offline: true);

        Assert.Equal(
            "dist/hello-a.sei",
            Assert.Single(new SeikoClient().ReadLock(fixture.Root)!.Sei, item => item.Name == "hello").OriginPath);
    }

    [Fact]
    public void Sync_FindsTheRightSnapshotWhenALongerNameIsNewer()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var client = new SeikoClient();
        var hello = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "base");
        var tools = fixture.WriteOrigin("hello-tools", "1.0.0", "tools.txt", "tools");
        client.Add(fixture.Root, Rel(fixture, hello), storeRoot: fixture.StoreDirectory);
        client.Add(fixture.Root, Rel(fixture, tools), storeRoot: fixture.StoreDirectory);

        // The hello-tools snapshot is now the newer one; a `hello-*.sei` glob
        // would match it and hand `hello` the wrong manifest.
        File.Delete(Path.Combine(fixture.Root, hello));
        client.Sync(fixture.Root, fixture.StoreDirectory, force: true);

        Assert.Equal("base", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void OfflinePrecheck_TerminatesOnACycleAndSharedDependencies()
    {
        using var fixture = new Fixture();
        // A local source keeps its indexes readable, so a cycle really is
        // walked instead of stopping at missing metadata.
        // x <-> y is a legal cycle; both are also reached twice (o -> x and
        // o -> z -> x) so a shared name must not be walked twice either.
        fixture.AddSei("x", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("y", null)],
            Artifacts = [new ArtifactFixture()],
        });
        fixture.AddSei("y", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("x", null)],
            Artifacts = [new ArtifactFixture()],
        });
        fixture.AddSei("z", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("x", null)],
            Artifacts = [new ArtifactFixture()],
        });
        fixture.WriteConfig("win-x64");
        var origin = fixture.WriteOrigin(
            "hello",
            "1.0.0",
            "hello.txt",
            "one",
            dependencies: [("x", null), ("z", null)]);

        // The initial names are not cached, and the walk must still finish.
        // The point is that it returns at all.
        new SeikoClient().Add(
            fixture.Root,
            Rel(fixture, origin),
            storeRoot: fixture.StoreDirectory,
            offline: true);

        Assert.Single(new SeikoClient().ReadLock(fixture.Root)!.Sei, item => item.Name == "hello");
    }

    [Fact]
    public void Add_RejectsAnUnknownPathThatFitsSeveralNames()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var client = new SeikoClient();
        client.Add(
            fixture.Root,
            Rel(fixture, fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "base")),
            storeRoot: fixture.StoreDirectory);
        var tools = fixture.WriteOrigin("hello-tools", "1.0.0", "tools.txt", "tools");
        client.Add(fixture.Root, Rel(fixture, tools), storeRoot: fixture.StoreDirectory);

        // Remove both declarations, so the stem alone has to identify the file.
        client.Remove(fixture.Root, "hello", storeRoot: fixture.StoreDirectory);
        client.Remove(fixture.Root, "hello-tools", storeRoot: fixture.StoreDirectory);
        File.Delete(Path.Combine(fixture.Root, tools));
        File.Delete(Path.Combine(fixture.Root, "dist/hello.sei"));

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root,
            "dist/hello-tools.sei",
            storeRoot: fixture.StoreDirectory));

        Assert.Equal("usage", error.Code);
        Assert.Contains("several origin names", error.Message);
    }

    [Fact]
    public void Add_RejectsAFileUrlInput()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var path = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "hi");
        var url = new Uri(Path.Combine(fixture.Root, path)).AbsoluteUri;

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root,
            url,
            storeRoot: fixture.StoreDirectory));

        Assert.Equal("usage", error.Code);
    }

    [Fact]
    public void Config_RejectsAFileUrlOriginPath()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var url = new Uri(Path.Combine(fixture.Root, fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "hi"))).AbsoluteUri;
        var config = Path.Combine(fixture.Root, "seiko.conf");
        File.WriteAllText(
            config,
            File.ReadAllText(config)
            + $"origin = [ {{ name = \"hello\", paths = [\"{url}\"], selectors = [ {{ rid = \"*\" }} ] }} ]\n"
            + "sei = [ { name = \"hello\" } ]\n");

        var error = Assert.Throws<SeikoException>(() => ConfigLoader.Load(fixture.Root));

        Assert.Equal("config", error.Code);
    }

    [Fact]
    public void Add_RejectsAnAbsoluteInputPath()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var absolute = Path.Combine(fixture.Root, fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one"));

        // Only relative input is accepted; an absolute path is rejected.
        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root,
            absolute,
            storeRoot: fixture.StoreDirectory));

        Assert.Equal("usage", error.Code);
        Assert.Contains("relative", error.Message);
        Assert.Empty(ConfigLoader.Load(fixture.Root).Origin);
    }

    private static string Rel(Fixture fixture, string relative)
        => Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.Combine(fixture.Root, relative));

    private static void WriteOriginConfig(
        Fixture fixture,
        string name,
        string path,
        string selectors,
        params string[] alsoDirect)
    {
        // Rewrite the sei block rather than appending: HOCON concatenates two
        // `sei` arrays, which would duplicate every entry.
        var config = Path.Combine(fixture.Root, "seiko.conf");
        var lines = File.ReadAllLines(config).ToList();
        var start = lines.FindIndex(line => line.StartsWith("sei =", StringComparison.Ordinal));
        if (start >= 0)
        {
            // The block ends at its `]` or, for a one-line `sei = []`, at once.
            var end = lines[start].Contains(']', StringComparison.Ordinal)
                ? start
                : lines.FindIndex(start, line => line.TrimStart().StartsWith(']'));
            lines.RemoveRange(start, end - start + 1);
        }

        lines.Add($"origin = [ {{ name = \"{name}\", paths = [{Quoted(path)}], {selectors} }} ]");
        lines.Add($"sei = [ {{ name = \"{name}\" }}"
            + string.Concat(alsoDirect.Select(SpecText))
            + " ]");
        File.WriteAllText(config, string.Join('\n', lines) + "\n");
    }

    // "lib@[1.0.0]" keeps its range; a bare name is declared without one.
    private static string SpecText(string text)
    {
        var at = text.IndexOf('@');
        return at < 0
            ? $", {{ name = \"{text}\" }}"
            : $", {{ name = \"{text[..at]}\", version = \"{text[(at + 1)..]}\" }}";
    }

    // One path is taken as-is; an explicit list (already quoted) passes through.
    private static string Quoted(string path)
        => path.StartsWith('"') ? path : $"\"{path}\"";

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('.', '/');
}
