using Seiko;
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

        // Both names must reach the same list: the walk continues past the
        // first gap instead of stopping there.
        Assert.Equal("source", error.Code);
        Assert.Contains("sei/a.json", error.Message);
        Assert.Contains("sei/b.json", error.Message);
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
    public void Add_UsesTheDeclarationForABareNameWhenTheStemFitsAnother()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var client = new SeikoClient();
        fixture.AddSei("hello", new VersionFixture { Version = "9.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteConfig("win-x64");
        var shared = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "base", fileName: "hello-tools-win.sei");
        client.Add(fixture.Root, Rel(fixture, shared), storeRoot: fixture.StoreDirectory);

        // The token is a bare name in the working directory, so it takes the
        // declaration branch; the copy there is hello's own artifact.
        var local = Path.Combine(Directory.GetCurrentDirectory(), "hello-tools-win.sei");
        File.Copy(Path.Combine(fixture.Root, shared), local, overwrite: true);
        try
        {
            var error = Assert.Throws<SeikoException>(
                () => client.Add(fixture.Root, "hello-tools-win.sei", storeRoot: fixture.StoreDirectory));
            // `hello` is a source package, so the message must offer it.
            Assert.Contains("'hello' for the source", error.Message);
        }
        finally
        {
            File.Delete(local);
        }
    }

    [Fact]
    public void Add_ReadsTheNewFileIdentityForADifferentDirectoryWithTheSameName()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var client = new SeikoClient();
        fixture.AddSei("hello", new VersionFixture { Version = "9.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteConfig("win-x64");
        // A different package whose file carries the same name, in another
        // directory. It is written and moved away first, because the hello
        // artifact below would otherwise overwrite it.
        Directory.CreateDirectory(Path.Combine(fixture.Root, "elsewhere"));
        File.Move(
            Path.Combine(fixture.Root, fixture.WriteOrigin("hello-tools", "1.0.0", "tools.txt", "tools", fileName: "hello-tools-win.sei")),
            Path.Combine(fixture.Root, "elsewhere/hello-tools-win.sei"));

        // `hello` is declared at dist/hello-tools-win.sei — the same basename.
        var shared = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "base", fileName: "hello-tools-win.sei");
        client.Add(fixture.Root, Rel(fixture, shared), storeRoot: fixture.StoreDirectory);

        // The token carries a path, so the declaration branch only applies if
        // the paths really are the same; here they are not, so the identity has
        // to come from the new file. Reusing `hello` would fail on the same
        // path twice; reading the bytes lands it on `hello-tools`.
        client.Add(fixture.Root, Rel(fixture, "elsewhere/hello-tools-win.sei"), storeRoot: fixture.StoreDirectory);

        Assert.Contains(
            ConfigLoader.Load(fixture.Root).Origin,
            origin => origin.Name == "hello-tools"
                && origin.Paths.Any(path => path.Contains("elsewhere", StringComparison.Ordinal)));

    }

    [Fact]
    public void OfflinePrecheck_IgnoresVersionsOutsideTheDeclaredRange()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        // lib 2.0.0 is the only version that depends on `extra`; the
        // declaration pins 1.0.0, so `extra` must not be required.
        fixture.AddSei(
            "lib",
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] },
            new VersionFixture
            {
                Version = "2.0.0",
                Dependencies = [new DepFixture("extra", null)],
                Artifacts = [new ArtifactFixture()],
            });
        fixture.AddSei("extra", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("lib", "[1.0.0]"));
        var origin = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one", dependencies: [("lib", null)]);

        // Prime the cache for lib and the origin, then drop only extra's, so a
        // pre-check that walked lib 2.0.0 would now fail.
        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true);

        Assert.Single(new SeikoClient().ReadLock(fixture.Root)!.Sei, item => item.Name == "lib");
    }

    [Fact]
    public void OfflinePrecheck_KeepsASelectorWhosePrerequisiteItSuppliesItself()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("b", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddSei("c", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        // The file's own artifact depends on B and C; its selector also wants B
        // to exist. B is therefore not in the graph yet when the selector is
        // judged, but the file supplies it itself.
        var origin = fixture.WriteOrigin(
            "hello",
            "1.0.0",
            "hello.txt",
            "one",
            dependencies: [("b", null), ("c", null)],
            dependencyLevel: "artifact");
        WriteOriginConfig(
            fixture,
            "hello",
            "\"dist/hello.sei\"",
            "selectors = [ { rid = \"*\", prerequisites = [ { name = \"b\", version = \"*\" } ] } ]",
            "b");

        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true);

        Assert.Single(new SeikoClient().ReadLock(fixture.Root)!.Sei, item => item.Name == "c");
    }

    [Fact]
    public void OfflinePrecheck_TreatsASelfSuppliedPrerequisiteAsReachable()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("b", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddSei("c", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        // B is not a direct declaration and is not cached; the file itself
        // depends on B and C, and its selector asks that B exist. Only a
        // candidate-aware check keeps the file, which then pulls both in.
        var origin = fixture.WriteOrigin(
            "hello",
            "1.0.0",
            "hello.txt",
            "one",
            dependencies: [("b", null), ("c", null)],
            dependencyLevel: "artifact");
        WriteOriginConfig(
            fixture,
            "hello",
            "\"dist/hello.sei\"",
            "selectors = [ { rid = \"*\", prerequisites = [ { name = \"b\", version = \"*\" } ] } ]");

        // Prime nothing; the pre-check must accept and the walk must report
        // B and C rather than skipping the only candidate.
        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root,
            Rel(fixture, origin),
            storeRoot: fixture.StoreDirectory,
            offline: true));

        Assert.Equal("source", error.Code);
        Assert.Contains("sei/b.json", error.Message);
        Assert.Contains("sei/c.json", error.Message);
    }

    [Fact]
    public void Add_DoesNotDeclareTheSameFileTwiceUnderDifferentSpellings()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var client = new SeikoClient();
        var path = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one");

        client.Add(fixture.Root, Rel(fixture, path), storeRoot: fixture.StoreDirectory);
        // `./dist/../dist/hello.sei` reaches the same file by another spelling;
        // the second add must refresh the condition, not append a duplicate.
        // Another spelling of the same file: the second add must refresh the
        // condition, not append a duplicate declaration.
        client.Add(fixture.Root, "./" + Rel(fixture, path), storeRoot: fixture.StoreDirectory);

        Assert.Single(Assert.Single(ConfigLoader.Load(fixture.Root).Origin).Paths);
    }

    [Fact]
    public void OfflinePrecheck_KeepsPerBranchRangesInsteadOfIntersectingThem()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        // B 2.0.0 needs X 1.0.0, B 1.0.0 needs X 2.0.0; Y and Z hang off the X
        // versions. Intersecting both B ranges for X yields no candidate at all.
        fixture.AddSei(
            "b",
            new VersionFixture
            {
                Version = "2.0.0",
                Dependencies = [new DepFixture("x", "[1.0.0]")],
                Artifacts = [new ArtifactFixture()],
            },
            new VersionFixture
            {
                Version = "1.0.0",
                Dependencies = [new DepFixture("x", "[2.0.0]")],
                Artifacts = [new ArtifactFixture()],
            });
        fixture.AddSei("x", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddSei("y", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddSei("z", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        var origin = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one", dependencies: [("b", null)]);

        // Prime everything, then keep only hello's and b's index caches: those
        // two stay resolvable, so the probe must get past them to notice X.
        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        var metadata = Path.Combine(fixture.StoreDirectory, ".metadata");
        var index = new Uri(server.BaseUrl.TrimEnd('/') + "/index.json");
        var keepB = MetadataCache.PathFor(fixture.StoreDirectory, new Uri(index, "sei/b.json").AbsoluteUri);
        var keepIndex = MetadataCache.PathFor(fixture.StoreDirectory, index.AbsoluteUri);
        var savedB = File.ReadAllBytes(keepB);
        var savedIndex = File.Exists(keepIndex) ? File.ReadAllBytes(keepIndex) : null;
        Directory.Delete(metadata, recursive: true);
        Directory.CreateDirectory(metadata);
        File.WriteAllBytes(keepB, savedB);
        if (savedIndex is not null)
        {
            File.WriteAllBytes(keepIndex, savedIndex);
        }

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root,
            Rel(fixture, origin),
            storeRoot: fixture.StoreDirectory,
            offline: true));

        Assert.Equal("source", error.Code);
        Assert.Equal("source", error.Code);
        Assert.Contains("sei/x.json", error.Message);
    }

    [Fact]
    public void OfflinePrecheck_AppliesALaterRangeToAnAlreadyWalkedName()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        // B 2.0.0 alone needs D. O is an origin that pins B to 1.0.0, and it is
        // only discovered after the direct declaration B; the range must still
        // take effect, so D must not be required.
        fixture.AddSei(
            "b",
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] },
            new VersionFixture
            {
                Version = "2.0.0",
                Dependencies = [new DepFixture("d", null)],
                Artifacts = [new ArtifactFixture()],
            });
        fixture.AddSei("d", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("b"));
        var origin = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one", dependencies: [("b", "[1.0.0]")]);

        // Prime b 1.0.0 and hello, but not d.
        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true);

        Assert.Equal("1.0.0", Assert.Single(
            new SeikoClient().ReadLock(fixture.Root)!.Sei,
            item => item.Name == "b").Version);
    }

    [Fact]
    public void OfflinePrecheck_ReportsASourceWhoseCacheIsMissingEvenWhenALaterSourceHasIt()
    {
        using var fixture = new Fixture();
        using var first = new TestHttpServer(fixture.SourceDirectory);
        var second = new Fixture();
        using var secondServer = new TestHttpServer(second.SourceDirectory);
        // Both sources answer x online, so first-source-wins picks A. Only B's
        // cache survives; the real offline run still asks A first and throws
        // before ever reaching B, so the pre-check must name A.
        fixture.AddSei("x", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["x.txt"] = "x" } }],
        });
        second.AddSei("x", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["x.txt"] = "x" } }],
        });
        WriteTwoSourceConfig(fixture, first.BaseUrl, second, secondServer.BaseUrl);
        var origin = fixture.WriteOrigin(
            "hello",
            "1.0.0",
            "hello.txt",
            "one",
            dependencies: [("x", null)]);

        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        File.Delete(MetadataCache.PathFor(
            fixture.StoreDirectory,
            first.BaseUrl.TrimEnd('/') + "/sei/x.json"));

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root,
            Rel(fixture, origin),
            storeRoot: fixture.StoreDirectory,
            offline: true));

        Assert.Equal("source", error.Code);
        Assert.Contains("sei/x.json from source 'first'", error.Message);
    }

    [Fact]
    public void OfflinePrecheck_TreatsACached404AsAnAnswerFromAnEarlierSource()
    {
        using var fixture = new Fixture();
        using var first = new TestHttpServer(fixture.SourceDirectory);
        var second = new Fixture();
        using var secondServer = new TestHttpServer(second.SourceDirectory);
        // A does not list x and answered 404 online; B has it. Offline, the
        // recorded 404 lets B answer, so the pre-check must not report A.
        second.AddSei("x", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["x.txt"] = "x" } }],
        });
        WriteTwoSourceConfig(fixture, first.BaseUrl, second, secondServer.BaseUrl);
        var origin = fixture.WriteOrigin(
            "hello",
            "1.0.0",
            "hello.txt",
            "one",
            dependencies: [("x", null)]);

        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        new SeikoClient().Add(
            fixture.Root,
            Rel(fixture, origin),
            storeRoot: fixture.StoreDirectory,
            offline: true);

        Assert.Single(new SeikoClient().ReadLock(fixture.Root)!.Sei, item => item.Name == "x");
    }

    [Fact]
    public void OfflinePrecheck_KeepsAnAll404AnswerAsNotFoundNotAsCacheGap()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("other", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        var origin = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one", dependencies: [("missing", null)]);
        // Prime online: the source answers 404 for `missing` and the record is
        // cached. A 404 is an answer, so offline must report the same "not
        // found" instead of demanding a cache entry that will never appear.
        Assert.Equal("resolve", Assert.Throws<SeikoException>(
            () => new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory)).Code);

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root,
            Rel(fixture, origin),
            storeRoot: fixture.StoreDirectory,
            offline: true));

        Assert.Contains("missing", error.Message);
        Assert.DoesNotContain("missing cached metadata", error.Message);
    }

    [Fact]
    public void OfflinePrecheck_ListsAGapRejectedByAnExactRangeInsteadOfStopping()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("b", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.AddSei("c", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        // b is not a direct declaration, so anything the pre-check invents for
        // it is only a guess; 1.0.0 would not satisfy [2.0.0] and both b and c
        // have to be reported.
        var origin = fixture.WriteOrigin(
            "hello",
            "1.0.0",
            "hello.txt",
            "one",
            dependencies: [("b", "[2.0.0]"), ("c", null)]);

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root,
            Rel(fixture, origin),
            storeRoot: fixture.StoreDirectory,
            offline: true));

        Assert.Equal("source", error.Code);
        Assert.Contains("sei/b.json", error.Message);
        Assert.Contains("sei/c.json", error.Message);
    }

    [Fact]
    public void OfflinePrecheck_ListsAGapRejectedByAnExclusiveLowerBound()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("b", new VersionFixture
        {
            Version = "3.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        // The bound itself is excluded, so a placeholder at the bound fails.
        var origin = fixture.WriteOrigin(
            "hello",
            "1.0.0",
            "hello.txt",
            "one",
            dependencies: [("b", "(2.0.0,4.0.0)")]);

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root,
            Rel(fixture, origin),
            storeRoot: fixture.StoreDirectory,
            offline: true));

        Assert.Equal("source", error.Code);
        Assert.Contains("sei/b.json", error.Message);
    }

    [Fact]
    public void OfflinePrecheck_UsesTheConfiguredRidNotTheHostOne()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("target-rid");
        // The host almost never has this rid, so a host-based choice finds no
        // matching selector. The run targets the configured rid, and the
        // pre-check has to choose the same file.
        fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one", rid: "target-rid");
        WriteOriginConfig(
            fixture,
            "hello",
            "\"dist/hello.sei\"",
            "selectors = [ { rid = \"target-rid\" } ]");

        new SeikoClient().Add(
            fixture.Root,
            Rel(fixture, "dist/hello.sei"),
            storeRoot: fixture.StoreDirectory,
            offline: true);

        Assert.Equal("1.0.0", Assert.Single(new SeikoClient().ReadLock(fixture.Root)!.Sei).Version);
    }

    [Fact]
    public void OfflinePrecheck_DoesNotRequireARootIndexForADependencyFreeOrigin()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("unused", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        var origin = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one");
        // The source was never contacted: no root index, no per-name cache.
        // The origin graph is closed, and a real offline run only reads names,
        // so the index is not needed.

        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true);

        Assert.False(File.Exists(MetadataCache.PathFor(
            fixture.StoreDirectory,
            server.BaseUrl.TrimEnd('/') + "/index.json")));
        Assert.Single(new SeikoClient().ReadLock(fixture.Root)!.Sei, item => item.Name == "hello");
    }

    [Fact]
    public void OfflinePrecheck_ResolvesARelativeLocalSourceAgainstTheInstallRoot()
    {
        using var fixture = new Fixture();
        fixture.AddSei("x", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["x.txt"] = "x" } }],
        });
        fixture.WriteConfig("win-x64");
        var config = Path.Combine(fixture.Root, "seiko.conf");
        File.WriteAllText(
            config,
            $"schemaVersion = {SeikoConfig.CurrentSchemaVersion}\n"
            + "sources = [ { id = \"local\", index = \"source\" } ]\n"
            + "rid = \"win-x64\"\n"
            + "sei = [ { name = \"x\", version = \"[1.0.0]\" } ]\n");
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        // The store lives under the install root but at a level the source
        // path does not repeat; resolving against the store would look for
        // <root>/store/source/sei/x.json.
        var result = client.Sync(
            fixture.Root,
            fixture.StoreDirectory,
            mode: SyncMode.Frozen,
            offline: true);

        Assert.Equal(1, result.SeiInstalled);
    }

    [Fact]
    public void OfflinePrecheck_ReportsTheDownstreamGapOfTheChosenBranch()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        // B 2.0.0 needs X 1.0.0, which needs Y; B 1.0.0 needs X 2.0.0, which
        // needs Z. The highest B wins, so only Y is reachable and Z must not
        // be required. Intersecting both X ranges first would leave X without
        // candidates and report X itself instead of Y.
        fixture.AddSei(
            "b",
            new VersionFixture
            {
                Version = "2.0.0",
                Dependencies = [new DepFixture("x", "[1.0.0]")],
                Artifacts = [new ArtifactFixture()],
            },
            new VersionFixture
            {
                Version = "1.0.0",
                Dependencies = [new DepFixture("x", "[2.0.0]")],
                Artifacts = [new ArtifactFixture()],
            });
        fixture.AddSei(
            "x",
            new VersionFixture
            {
                Version = "1.0.0",
                Dependencies = [new DepFixture("y", null)],
                Artifacts = [new ArtifactFixture()],
            },
            new VersionFixture
            {
                Version = "2.0.0",
                Dependencies = [new DepFixture("z", null)],
                Artifacts = [new ArtifactFixture()],
            });
        fixture.AddSei("y", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddSei("z", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        var origin = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one", dependencies: [("b", null)]);
        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        var cache = Path.Combine(fixture.StoreDirectory, ".metadata");
        var index = new Uri(server.BaseUrl.TrimEnd('/') + "/index.json");
        var keep = new[] { "b", "x" }
            .Select(name => MetadataCache.PathFor(fixture.StoreDirectory, new Uri(index, $"sei/{name}.json").AbsoluteUri))
            .ToDictionary(path => path, File.ReadAllBytes);
        var root = MetadataCache.PathFor(fixture.StoreDirectory, index.AbsoluteUri);
        var savedRoot = File.Exists(root) ? File.ReadAllBytes(root) : null;
        Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);
        foreach (var (path, bytes) in keep)
        {
            File.WriteAllBytes(path, bytes);
        }

        if (savedRoot is not null)
        {
            File.WriteAllBytes(root, savedRoot);
        }

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root,
            Rel(fixture, origin),
            storeRoot: fixture.StoreDirectory,
            offline: true));

        // Both X versions are read before a leaf is reached, so both their
        // downstream names are real gaps: the walk shows the whole evidence
        // instead of betting on one branch.
        Assert.Equal("source", error.Code);
        Assert.Contains("sei/y.json", error.Message);
        Assert.Contains("sei/z.json", error.Message);
        Assert.DoesNotContain("sei/x.json", error.Message);
    }

    [Fact]
    public void OfflinePrecheck_ReportsBothSourcesWhenBothAreUnknown()
    {
        using var fixture = new Fixture();
        using var first = new TestHttpServer(fixture.SourceDirectory);
        var second = new Fixture();
        using var secondServer = new TestHttpServer(second.SourceDirectory);
        // Both caches for x are gone. The real run stops at the first one, but
        // the second is a second round trip after refilling, so it is a gap
        // too — not something a single refetch makes good.
        fixture.AddSei("x", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        second.AddSei("x", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        WriteTwoSourceConfig(fixture, first.BaseUrl, second, secondServer.BaseUrl);
        var origin = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one", dependencies: [("x", null)]);
        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        var cache = Path.Combine(fixture.StoreDirectory, ".metadata");
        var index = new Uri(first.BaseUrl.TrimEnd('/') + "/index.json");
        var kept = MetadataCache.PathFor(fixture.StoreDirectory, index.AbsoluteUri);
        var saved = File.ReadAllBytes(kept);
        Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(kept, saved);

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true));

        Assert.Equal(
            new[] { "sei/x.json from source 'first'", "sei/x.json from source 'second'" },
            Gaps(error.Message));
    }

    [Fact]
    public void OfflinePrecheck_KeepsAGapReadBeforeAChecksumFailure()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("x", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        // `hello` is intact and depends on x and gone. x is unknown (a gap);
        // gone is an origin declaration whose file and snapshot are both
        // absent, so reading it fails with checksum. The x gap is read first
        // and must survive that later failure.
        fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one", dependencies: [("gone", null)]);
        fixture.WriteOrigin("gone", "1.0.0", "gone.txt", "gone");
        // x is declared first, so it is read (and recorded) before gone fails.
        var config = Path.Combine(fixture.Root, "seiko.conf");
        File.WriteAllText(
            config,
            $"schemaVersion = {SeikoConfig.CurrentSchemaVersion}\n"
            + "sources = [ { id = \"http\", index = \"" + server.BaseUrl.TrimEnd('/') + "/index.json\" } ]\n"
            + "rid = \"win-x64\"\n"
            + "origin = [\n"
            + "  { name = \"hello\", paths = [\"dist/hello.sei\"], selectors = [ { rid = \"*\" } ] },\n"
            + "  { name = \"gone\", paths = [\"dist/gone.sei\"], selectors = [ { rid = \"*\" } ] } ]\n"
            + "sei = [ { name = \"x\", version = \"[1.0.0]\" }, { name = \"gone\", version = \"[1.0.0]\" }, { name = \"hello\" } ]\n");
        File.Delete(Path.Combine(fixture.Root, "dist", "gone.sei"));

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root, Rel(fixture, "dist/hello.sei"), storeRoot: fixture.StoreDirectory, offline: true));

        // The checksum failure is still the result, and the gap read before it
        // is part of the message instead of being thrown away.
        Assert.Equal("checksum", error.Code);
        Assert.Contains("gone", error.Message);
        Assert.Contains("sei/x.json from source 'http'", error.Message);
    }

    [Fact]
    public void OfflinePrecheck_FallsBackWhenTwoKnownRangesContradictOnAnUnknownName()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        // A 2.0.0 needs X [1.0.0]; A 1.0.0 needs Y; B needs X [2.0.0]. X is
        // unknown, so its invented version satisfies both ranges at once and
        // the walk would stop at A 2.0.0 with X reported. But no single X can
        // satisfy both, so the resolver must fall back to A 1.0.0 and reach Y.
        fixture.AddSei(
            "a",
            new VersionFixture
            {
                Version = "2.0.0",
                Dependencies = [new DepFixture("x", "[1.0.0]")],
                Artifacts = [new ArtifactFixture()],
            },
            new VersionFixture
            {
                Version = "1.0.0",
                Dependencies = [new DepFixture("y", null)],
                Artifacts = [new ArtifactFixture()],
            });
        fixture.AddSei(
            "b",
            new VersionFixture
            {
                Version = "1.0.0",
                Dependencies = [new DepFixture("x", "[2.0.0]")],
                Artifacts = [new ArtifactFixture()],
            });
        // Online the resolver must backtrack to A 1.0.0 to satisfy x 2.0.0;
        // only that branch keeps the graph solvable.
        fixture.AddSei(
            "x",
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] },
            new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddSei("y", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        // b first: its x [2.0.0] is read before a 2.0.0 adds x [1.0.0]. The
        // lock is removed afterwards, so its version hint cannot put 1.0.0
        // back in front of the candidates.
        var origin = fixture.WriteOrigin(
            "hello", "1.0.0", "hello.txt", "one", dependencies: [("b", null), ("a", null)]);
        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        File.Delete(Path.Combine(fixture.Root, "seiko.lock.json"));
        var cache = Path.Combine(fixture.StoreDirectory, ".metadata");
        var index = new Uri(server.BaseUrl.TrimEnd('/') + "/index.json");
        var keep = new[] { "a", "b" }
            .Select(name => MetadataCache.PathFor(fixture.StoreDirectory, new Uri(index, $"sei/{name}.json").AbsoluteUri))
            .ToDictionary(path => path, File.ReadAllBytes);
        var root = MetadataCache.PathFor(fixture.StoreDirectory, index.AbsoluteUri);
        var savedRoot = File.ReadAllBytes(root);
        Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);
        foreach (var (path, bytes) in keep)
        {
            File.WriteAllBytes(path, bytes);
        }

        File.WriteAllBytes(root, savedRoot);

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true));

        Assert.Equal(
            new[] { "sei/x.json from source 'http'", "sei/y.json from source 'http'" },
            Gaps(error.Message));
    }

    [Fact]
    public void OfflinePrecheck_LockedResolvesInsteadOfOnlyReplayingTheLock()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("a", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddSei("b", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("a", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        // `b` is declared afterwards, so it is not in the lock. `--locked`
        // resolves normally and only then compares, so b's cache is needed —
        // a check that walks the lock alone would find nothing wrong.
        var config = Path.Combine(fixture.Root, "seiko.conf");
        File.WriteAllText(
            config,
            File.ReadAllText(config).Replace(
                "sei = [\n  { name = \"a\", version = \"[1.0.0]\" }\n]",
                "sei = [\n  { name = \"a\", version = \"[1.0.0]\" },\n  { name = \"b\", version = \"[1.0.0]\" }\n]",
                StringComparison.Ordinal));
        File.Delete(MetadataCache.PathFor(fixture.StoreDirectory, server.BaseUrl.TrimEnd('/') + "/sei/b.json"));

        var error = Assert.Throws<SeikoException>(() => client.Sync(
            fixture.Root, fixture.StoreDirectory, mode: SyncMode.Locked, offline: true));

        Assert.Equal(["sei/b.json from source 'http'"], Gaps(error.Message));
    }

    [Fact]
    public void OfflinePrecheck_FrozenCoversTheCrossSourceReads()
    {
        using var fixture = new Fixture();
        using var first = new TestHttpServer(fixture.SourceDirectory);
        var second = new Fixture();
        using var secondServer = new TestHttpServer(second.SourceDirectory);
        // The frozen replay resolves nothing, but it still cross-checks every
        // locked entry against the other sources; a cold cache there fails the
        // run, so it is a gap.
        fixture.AddSei("x", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        second.AddSei("x", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        WriteTwoSourceConfig(fixture, first.BaseUrl, second, secondServer.BaseUrl);
        var origin = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one", dependencies: [("x", null)]);
        var client = new SeikoClient();
        client.Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        var cache = Path.Combine(fixture.StoreDirectory, ".metadata");
        var index = new Uri(first.BaseUrl.TrimEnd('/') + "/index.json");
        var keptIndex = MetadataCache.PathFor(fixture.StoreDirectory, index.AbsoluteUri);
        var keptX = MetadataCache.PathFor(fixture.StoreDirectory, new Uri(index, "sei/x.json").AbsoluteUri);
        var savedIndex = File.ReadAllBytes(keptIndex);
        var savedX = File.ReadAllBytes(keptX);
        Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(keptIndex, savedIndex);
        File.WriteAllBytes(keptX, savedX);

        var error = Assert.Throws<SeikoException>(() => client.Sync(
            fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen, offline: true));

        Assert.Equal(["sei/x.json from source 'second'"], Gaps(error.Message));
    }

    [Fact]
    public void OfflinePrecheck_FrozenTellsARecordedAbsenceApartFromAColdCache()
    {
        using var fixture = new Fixture();
        using var first = new TestHttpServer(fixture.SourceDirectory);
        var second = new Fixture();
        using var secondServer = new TestHttpServer(second.SourceDirectory);
        // first answers 404 for x; second supplies it. Frozen can never
        // replay this (the lock would name second), but the reason matters:
        // first's cache is not missing, the name is simply not there.
        second.AddSei("x", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        WriteTwoSourceConfig(fixture, first.BaseUrl, second, secondServer.BaseUrl);
        var origin = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one", dependencies: [("x", null)]);
        var client = new SeikoClient();
        client.Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        var lockPath = Path.Combine(fixture.Root, "seiko.lock.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(lockPath))!;
        node["sei"]!.AsArray().Single(item => item!["name"]!.GetValue<string>() == "x")!["source"] = "first";
        File.WriteAllText(lockPath, node.ToJsonString());

        var error = Assert.Throws<SeikoException>(() => client.Sync(
            fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen, offline: true));

        // A recorded 404 is not a cache gap: refetching may answer 404 again,
        // so the replay fails with the lock's own code and the replay's own
        // wording, exactly as the real frozen run would.
        Assert.Equal("lock", error.Code);
        Assert.Contains("x is not in source 'first'", error.Message);
    }

    [Fact]
    public void OfflinePrecheck_AddWithRemovalReportsEveryGapBeforeResolving()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("old", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddSei("x", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddSei("y", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("old", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        // Adding a cold origin next to a removal makes the operation resolve
        // once before SyncLocked; both gaps must already be summarised there.
        var origin = fixture.WriteOrigin(
            "hello", "1.0.0", "hello.txt", "one", dependencies: [("x", null), ("y", null)]);
        var cache = Path.Combine(fixture.StoreDirectory, ".metadata");
        Directory.Delete(cache, recursive: true);

        var error = Assert.Throws<SeikoException>(() => client.AddMany(
            fixture.Root,
            [Rel(fixture, origin)],
            remove: ["old"],
            storeRoot: fixture.StoreDirectory,
            offline: true));

        Assert.Equal("source", error.Code);
        Assert.Equal(
            new[] { "sei/x.json from source 'http'", "sei/y.json from source 'http'" },
            Gaps(error.Message));
    }

    [Fact]
    public void OfflinePrecheck_DoesNotRequireAColdDependencyOfALowerVersion()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei(
            "a",
            new VersionFixture
            {
                Version = "2.0.0",
                Artifacts = [new ArtifactFixture()],
            },
            new VersionFixture
            {
                Version = "1.0.0",
                Dependencies = [new DepFixture("y", null)],
                Artifacts = [new ArtifactFixture()],
            });
        fixture.AddSei("y", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        var origin = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one", dependencies: [("a", null)]);
        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        var cache = Path.Combine(fixture.StoreDirectory, ".metadata");
        var index = new Uri(server.BaseUrl.TrimEnd('/') + "/index.json");
        var keepA = MetadataCache.PathFor(fixture.StoreDirectory, new Uri(index, "sei/a.json").AbsoluteUri);
        var savedA = File.ReadAllBytes(keepA);
        var root = MetadataCache.PathFor(fixture.StoreDirectory, index.AbsoluteUri);
        var savedRoot = File.ReadAllBytes(root);
        Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(keepA, savedA);
        File.WriteAllBytes(root, savedRoot);

        // a 2.0.0 is complete and wins on its own; y is only reachable through
        // a 1.0.0, so a whole-graph union would demand it for nothing.
        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true);

        Assert.Equal("2.0.0", Assert.Single(
            new SeikoClient().ReadLock(fixture.Root)!.Sei, item => item.Name == "a").Version);
    }

    [Fact]
    public void OfflinePrecheck_DoesNotCarryConstraintsOverFromAFailedBranch()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        // x is read first (unknown). A 2.0.0 then adds x [1.0.0] and fails on
        // the missing z; the restored branch must not keep that range, or
        // A 1.0.0's x [2.0.0] would contradict it before y is even read.
        fixture.AddSei(
            "a",
            new VersionFixture
            {
                Version = "2.0.0",
                Dependencies = [new DepFixture("x", "[1.0.0]"), new DepFixture("z", null)],
                Artifacts = [new ArtifactFixture()],
            },
            new VersionFixture
            {
                Version = "1.0.0",
                Dependencies = [new DepFixture("x", "[2.0.0]"), new DepFixture("y", null)],
                Artifacts = [new ArtifactFixture()],
            });
        fixture.AddSei(
            "x",
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] },
            new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddSei("y", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        var origin = fixture.WriteOrigin(
            "hello", "1.0.0", "hello.txt", "one", dependencies: [("x", null), ("a", null)]);
        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        var cache = Path.Combine(fixture.StoreDirectory, ".metadata");
        var index = new Uri(server.BaseUrl.TrimEnd('/') + "/index.json");
        // Only a stays cached; x must stay unknown, or it is not a placeholder.
        var keep = new[] { "a" }
            .Select(name => MetadataCache.PathFor(fixture.StoreDirectory, new Uri(index, $"sei/{name}.json").AbsoluteUri))
            .ToDictionary(path => path, File.ReadAllBytes);
        var root = MetadataCache.PathFor(fixture.StoreDirectory, index.AbsoluteUri);
        var savedRoot = File.ReadAllBytes(root);
        Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);
        foreach (var (path, bytes) in keep)
        {
            File.WriteAllBytes(path, bytes);
        }

        File.WriteAllBytes(root, savedRoot);

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true));

        Assert.Equal(
            new[] { "sei/x.json from source 'http'", "sei/y.json from source 'http'", "sei/z.json from source 'http'" },
            Gaps(error.Message));
    }

    [Fact]
    public void OfflinePrecheck_DoesNotLetAFailedArtifactProbeBorrowTheParentLeaf()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        // w stays unknown, so the leaf keeps being a guess and the search
        // really reaches c's second version. a has two equally specific
        // artifacts; a1 conflicts with z and can never be solved, a0 can. The
        // ambiguity probe for the later c branch therefore runs while a parent
        // leaf exists, and still has to find a1 dead on its own.
        fixture.AddSei("a", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture { Files = { ["a.txt"] = "a" } },
                new ArtifactFixture { Conflicts = [new DepFixture("z", null)] },
            ],
        });
        fixture.AddSei(
            "c",
            new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture { Files = { ["c2.txt"] = "2" } }] },
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["c1.txt"] = "1" } }] });
        fixture.AddSei("z", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        fixture.AddSei("w", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        var origin = fixture.WriteOrigin(
            "hello", "1.0.0", "hello.txt", "one", dependencies: [("a", null), ("c", null), ("z", null), ("w", null)]);
        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);

        // Prime everything except w, so the leaf carries a placeholder.
        var cache = Path.Combine(fixture.StoreDirectory, ".metadata");
        var index = new Uri(server.BaseUrl.TrimEnd('/') + "/index.json");
        var keep = new[] { "a", "c", "z" }
            .Select(name => MetadataCache.PathFor(fixture.StoreDirectory, new Uri(index, $"sei/{name}.json").AbsoluteUri))
            .ToDictionary(path => path, File.ReadAllBytes);
        var root = MetadataCache.PathFor(fixture.StoreDirectory, index.AbsoluteUri);
        var savedRoot = File.ReadAllBytes(root);
        Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);
        foreach (var (path, bytes) in keep)
        {
            File.WriteAllBytes(path, bytes);
        }

        File.WriteAllBytes(root, savedRoot);

        // w is the only gap. A false ambiguity would be raised instead, with
        // no gap list at all.
        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true));

        Assert.Equal("source", error.Code);
        Assert.Equal(["sei/w.json from source 'http'"], Gaps(error.Message));
        Assert.DoesNotContain("ambiguous", error.Message);
    }

    [Fact]
    public void OfflinePrecheck_SucceedsOnACompleteMultiCandidateGraph()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        // Twenty independent declarations with two versions each, fully
        // cached. Enumerating every combination must not run out of budget.
        var names = Enumerable.Range(0, 20).Select(index => $"p{index:00}").ToList();
        foreach (var name in names)
        {
            fixture.AddSei(
                name,
                new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] },
                new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture()] });
        }

        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        var origin = fixture.WriteOrigin(
            "hello", "1.0.0", "hello.txt", "one", dependencies: [.. names.Select(name => (name, (string?)null))]);
        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);

        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true);

        Assert.Equal(21, new SeikoClient().ReadLock(fixture.Root)!.Sei.Count);
    }

    [Fact]
    public void OfflinePrecheck_FrozenDoesNotTreatAMissingLocalFileAsACacheGap()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        var other = Path.Combine(fixture.Root, "other-index");
        Directory.CreateDirectory(Path.Combine(other, "sei"));
        File.WriteAllBytes(
            Path.Combine(other, "index.json"),
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new IndexRoot { Sei = [] }, SeikoJsonContext.Default.IndexRoot));
        fixture.AddSei("a", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("a", "[1.0.0]"));
        // A local source that never has the name is part of the cross-source
        // walk; its missing file is not something to refill.
        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            $"schemaVersion = {SeikoConfig.CurrentSchemaVersion}\n"
            + $"sources = [ {{ id = \"http\", index = \"{server.BaseUrl}index.json\" }}, "
            + $"{{ id = \"other\", index = \"{other.Replace('\\', '/')}/index.json\" }} ]\n"
            + "rid = \"win-x64\"\nsei = [ { name = \"a\", version = \"[1.0.0]\" } ]\n");
        var client = new SeikoClient();
        // The priming sync already trips over the broken source.
        try
        {
            client.Sync(fixture.Root, fixture.StoreDirectory);
        }
        catch (SeikoException)
        {
        }

        var result = client.Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen, offline: true);

        Assert.Equal(1, result.SeiInstalled);
    }

    [Fact]
    public void OfflinePrecheck_ReportsEveryBareNameGap()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("one", new VersionFixture { Version = "9.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddSei("two", new VersionFixture { Version = "9.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        // Two bare files whose names are also source packages. The ambiguity
        // check reads both names before the run; with a cold cache both are
        // gaps, not just the first.
        var one = fixture.WriteOrigin("one", "1.0.0", "one.txt", "one", fileName: "one.sei");
        var two = fixture.WriteOrigin("two", "1.0.0", "two.txt", "two", fileName: "two.sei");
        var working = Directory.GetCurrentDirectory();
        var localOne = Path.Combine(working, "one.sei");
        var localTwo = Path.Combine(working, "two.sei");
        File.Copy(Path.Combine(fixture.Root, one), localOne, overwrite: true);
        File.Copy(Path.Combine(fixture.Root, two), localTwo, overwrite: true);
        try
        {
            var error = Assert.Throws<SeikoException>(() => new SeikoClient().AddMany(
                fixture.Root, ["one.sei", "two.sei"], remove: null, storeRoot: fixture.StoreDirectory, offline: true));

            Assert.Equal(
                new[] { "sei/one.json from source 'http'", "sei/two.json from source 'http'" },
                Gaps(error.Message));
        }
        finally
        {
            File.Delete(localOne);
            File.Delete(localTwo);
        }
    }

    [Fact]
    public void OfflinePrecheck_InfoStopsAtTheFirstSourceThatHasTheName()
    {
        using var fixture = new Fixture();
        using var first = new TestHttpServer(fixture.SourceDirectory);
        var second = new Fixture();
        using var secondServer = new TestHttpServer(second.SourceDirectory);
        // The first source answers x; the second is cold. `info` stops at the
        // first hit, so the second source's cache is not required.
        fixture.AddSei("x", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        second.AddSei("x", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        WriteTwoSourceConfig(fixture, first.BaseUrl, second, secondServer.BaseUrl);
        // Declare x so the priming sync actually fetches it.
        var config = Path.Combine(fixture.Root, "seiko.conf");
        File.WriteAllText(
            config,
            File.ReadAllText(config).Replace("sei = []", "sei = [ { name = \"x\", version = \"[1.0.0]\" } ]", StringComparison.Ordinal));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        // Keep the first source's answer only; the later one goes cold.
        var secondPath = MetadataCache.PathFor(
            fixture.StoreDirectory, secondServer.BaseUrl.TrimEnd('/') + "/sei/x.json");
        File.Delete(secondPath);

        var info = client.Info(fixture.Root, "x", offline: true, storeRoot: fixture.StoreDirectory);

        Assert.Equal("x", info.Name);
    }

    [Fact]
    public void OfflinePrecheck_ConflictWithAnUnknownNameNarrowsTheBranch()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        // b 2.0.0 conflicts with x [2.0.0] and x is unknown; b 1.0.0 does not.
        // A branch that pins x to [2.0.0] therefore cannot take b 2.0.0, so
        // the walk has to reach b 1.0.0 and report y.
        fixture.AddSei(
            "b",
            new VersionFixture
            {
                Version = "2.0.0",
                Conflicts = [new DepFixture("x", "[2.0.0]")],
                Artifacts = [new ArtifactFixture()],
            },
            new VersionFixture
            {
                Version = "1.0.0",
                Dependencies = [new DepFixture("y", null)],
                Artifacts = [new ArtifactFixture()],
            });
        fixture.AddSei(
            "x",
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] },
            new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddSei("y", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        var origin = fixture.WriteOrigin(
            "hello", "1.0.0", "hello.txt", "one", dependencies: [("x", "[2.0.0]"), ("b", null)]);
        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        var cache = Path.Combine(fixture.StoreDirectory, ".metadata");
        var index = new Uri(server.BaseUrl.TrimEnd('/') + "/index.json");
        var keep = MetadataCache.PathFor(fixture.StoreDirectory, new Uri(index, "sei/b.json").AbsoluteUri);
        var saved = File.ReadAllBytes(keep);
        var root = MetadataCache.PathFor(fixture.StoreDirectory, index.AbsoluteUri);
        var savedRoot = File.ReadAllBytes(root);
        Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(keep, saved);
        File.WriteAllBytes(root, savedRoot);

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true));

        Assert.Equal(["sei/x.json from source 'http'", "sei/y.json from source 'http'"], Gaps(error.Message));
    }

    [Fact]
    public void OfflinePrecheck_KeepsTheUnknownOfAnEarlierSourceWhenALaterOneAnswers()
    {
        using var fixture = new Fixture();
        using var first = new TestHttpServer(fixture.SourceDirectory);
        var second = new Fixture();
        using var secondServer = new TestHttpServer(second.SourceDirectory);
        // First source: o and nothing else cached. Second source: a 2.0 cached.
        // The probe takes a from the second source and would call that a
        // proven answer — but the first source wins whenever it has a, and if
        // it only has 1.0 the run falls back to o 1.0 and reads y.
        fixture.AddSei(
            "o",
            new VersionFixture
            {
                Version = "2.0.0",
                Dependencies = [new DepFixture("a", "[2.0.0,3.0.0)")],
                Artifacts = [new ArtifactFixture()],
            },
            new VersionFixture
            {
                Version = "1.0.0",
                Dependencies = [new DepFixture("y", null)],
                Artifacts = [new ArtifactFixture()],
            });
        second.AddSei("a", new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture()] });
        WriteTwoSourceConfig(fixture, first.BaseUrl, second, secondServer.BaseUrl);
        var origin = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one", dependencies: [("o", null)]);
        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        var cache = Path.Combine(fixture.StoreDirectory, ".metadata");
        var firstIndex = new Uri(first.BaseUrl.TrimEnd('/') + "/index.json");
        var secondIndex = new Uri(secondServer.BaseUrl.TrimEnd('/') + "/index.json");
        var keep = new[]
        {
            MetadataCache.PathFor(fixture.StoreDirectory, firstIndex.AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, new Uri(firstIndex, "sei/o.json").AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, secondIndex.AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, new Uri(secondIndex, "sei/a.json").AbsoluteUri),
        }.ToDictionary(path => path, File.ReadAllBytes);
        Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);
        foreach (var (path, bytes) in keep)
        {
            File.WriteAllBytes(path, bytes);
        }

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true));

        // o comes only from the first source; y is unknown on both, and the
        // second source's answer for a is not the proven one.
        Assert.Equal(
            new[]
            {
                "sei/a.json from source 'first'",
                "sei/o.json from source 'second'",
                "sei/y.json from source 'first'",
                "sei/y.json from source 'second'",
            },
            Gaps(error.Message));
    }

    [Fact]
    public void OfflinePrecheck_SkipsTheDependenciesOfAProvenDeadArtifact()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei(
            "b",
            new VersionFixture
            {
                Version = "2.0.0",
                Artifacts =
                [
                    new ArtifactFixture
                    {
                        Conflicts = [new DepFixture("x", "*")],
                        Dependencies = [new DepFixture("y", null)],
                    },
                ],
            },
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts = [new ArtifactFixture()],
            });
        fixture.AddSei(
            "x",
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddSei("y", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        // x is unknown; b 2.0's only artifact conflicts with any x, so the
        // real run drops it as soon as x is assigned and never reads y.
        var origin = fixture.WriteOrigin(
            "hello", "1.0.0", "hello.txt", "one", dependencies: [("x", null), ("b", null)]);
        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        var cache = Path.Combine(fixture.StoreDirectory, ".metadata");
        var index = new Uri(server.BaseUrl.TrimEnd('/') + "/index.json");
        var keep = MetadataCache.PathFor(fixture.StoreDirectory, new Uri(index, "sei/b.json").AbsoluteUri);
        var saved = File.ReadAllBytes(keep);
        var root = MetadataCache.PathFor(fixture.StoreDirectory, index.AbsoluteUri);
        var savedRoot = File.ReadAllBytes(root);
        Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(keep, saved);
        File.WriteAllBytes(root, savedRoot);

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true));

        // x is the only gap: y belongs to the artifact that was pruned.
        Assert.Equal("source", error.Code);
        Assert.Equal(["sei/x.json from source 'http'"], Gaps(error.Message));
    }

    [Fact]
    public void OfflinePrecheck_ConflictExclusionsAreJudgedAsAUnion()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        var second = new Fixture();
        using var secondServer = new TestHttpServer(second.SourceDirectory);
        // The declaration pins x to [1,3). A and B each exclude one half;
        // together they cover the whole range although neither does alone.
        // a and b also live in a second, cold source: whether that source is
        // read at all tells a real pruning apart from merely walking on.
        fixture.AddSei(
            "a",
            new VersionFixture
            {
                Version = "1.0.0",
                Conflicts = [new DepFixture("x", "[1.0.0,2.0.0)")],
                Artifacts = [new ArtifactFixture()],
            });
        fixture.AddSei(
            "b",
            new VersionFixture
            {
                Version = "1.0.0",
                Conflicts = [new DepFixture("x", "[2.0.0,3.0.0)")],
                Artifacts = [new ArtifactFixture()],
            });
        fixture.AddSei(
            "x",
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] },
            new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture()] });
        second.AddSei("a", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        second.AddSei("b", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        WriteTwoSourceConfig(fixture, server.BaseUrl, second, secondServer.BaseUrl);
        var origin = fixture.WriteOrigin(
            "hello", "1.0.0", "hello.txt", "one",
            dependencies: [("x", "[1.0.0,3.0.0)"), ("a", null), ("b", null)]);
        // Online the graph is already unsolvable: both conflicts hold and no
        // x satisfies them together. The metadata still gets cached first.
        Assert.Throws<SeikoException>(
            () => new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory));
        var cache = Path.Combine(fixture.StoreDirectory, ".metadata");
        var index = new Uri(server.BaseUrl.TrimEnd('/') + "/index.json");
        var keep = new[] { "a", "b" }
            .Select(name => MetadataCache.PathFor(fixture.StoreDirectory, new Uri(index, $"sei/{name}.json").AbsoluteUri))
            .ToDictionary(path => path, File.ReadAllBytes);
        var root = MetadataCache.PathFor(fixture.StoreDirectory, index.AbsoluteUri);
        var savedRoot = File.ReadAllBytes(root);
        Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);
        foreach (var (path, bytes) in keep)
        {
            File.WriteAllBytes(path, bytes);
        }

        File.WriteAllBytes(root, savedRoot);

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true));

        // Accepted, the fake leaf would be a plain gap result ("source").
        // Rejected, the walk runs out of versions and the conflict is the
        // answer, with the gaps beside it ("resolve").
        Assert.Equal("resolve", error.Code);
        Assert.Contains("sei/x.json from source 'first'", error.Message);
        Assert.Contains("sei/x.json from source 'second'", error.Message);
    }

    [Fact]
    public void OfflinePrecheck_FrozenReportsASourceWhoseIndexIsGone()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("a", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("a", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        // The locked local source's index disappears. The real run fails
        // opening the source, not on a missing package inside it.
        var local = Path.Combine(fixture.Root, "local-index");
        Directory.CreateDirectory(Path.Combine(local, "sei"));
        File.Copy(
            Path.Combine(fixture.SourceDirectory, "index.json"),
            Path.Combine(local, "index.json"),
            overwrite: true);
        File.Copy(
            Path.Combine(fixture.SourceDirectory, "sei", "a.json"),
            Path.Combine(local, "sei", "a.json"),
            overwrite: true);
        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            $"schemaVersion = {SeikoConfig.CurrentSchemaVersion}\n"
            + $"sources = [ {{ id = \"local\", index = \"{local.Replace('\\', '/')}/index.json\" }} ]\n"
            + "rid = \"win-x64\"\nsei = [ { name = \"a\", version = \"[1.0.0]\" } ]\n");
        var lockPath = Path.Combine(fixture.Root, "seiko.lock.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(lockPath))!;
        node["sei"]!.AsArray()[0]!["source"] = "local";
        File.WriteAllText(lockPath, node.ToJsonString());
        File.Delete(Path.Combine(local, "index.json"));

        var error = Assert.Throws<SeikoException>(() => client.Sync(
            fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen, offline: true));

        Assert.Equal("source", error.Code);
        Assert.Contains("index not found", error.Message);
    }

    [Fact]
    public void OfflinePrecheck_AddReportsResolutionAndDisambiguationGapsTogether()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("x", new VersionFixture { Version = "9.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddSei("y", new VersionFixture { Version = "9.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        // x.sei is local, so the resolution pre-check never needs the source's
        // x; the bare-token check does. y is an explicit cold name. Both gaps
        // belong in one list.
        fixture.WriteOrigin("x", "1.0.0", "x.txt", "one", fileName: "x.sei");
        var working = Directory.GetCurrentDirectory();
        var local = Path.Combine(working, "x.sei");
        File.Copy(Path.Combine(fixture.Root, "dist/x.sei"), local, overwrite: true);
        try
        {
            // `y` is a name, so only a preview may run offline; the gaps are
            // the same either way.
            var error = Assert.Throws<SeikoException>(() => new SeikoClient().AddMany(
                fixture.Root, ["x.sei", "y"], remove: null,
                storeRoot: fixture.StoreDirectory, dryRun: true, offline: true));

            Assert.Equal(
                new[] { "sei/x.json from source 'http'", "sei/y.json from source 'http'" },
                Gaps(error.Message));
        }
        finally
        {
            File.Delete(local);
        }
    }

    [Fact]
    public void OfflinePrecheck_InfoPrefersAnOriginOverAColdSource()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("unused", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        // An origin declaration answers before any source and needs no cache.
        fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one");
        WriteOriginConfig(fixture, "hello", "dist/hello.sei", "selectors = [ { rid = \"*\" } ]");

        var info = new SeikoClient().Info(fixture.Root, "hello", offline: true, storeRoot: fixture.StoreDirectory);

        Assert.Equal("hello", info.Name);
    }

    [Fact]
    public void OfflinePrecheck_KeepsSourceUncertaintyAcrossAnArtifactBacktrack()
    {
        using var fixture = new Fixture();
        using var first = new TestHttpServer(fixture.SourceDirectory);
        var second = new Fixture();
        using var secondServer = new TestHttpServer(second.SourceDirectory);
        // a only exists in the second source here, so it is a shadowed answer.
        // a 2 has two artifacts: the first needs a missing z, the second has
        // no relations. Backtracking between them must not clear the shadow,
        // or the leaf looks proven and o 1 (with y) is never reached.
        fixture.AddSei(
            "o",
            new VersionFixture
            {
                Version = "2.0.0",
                Dependencies = [new DepFixture("a", "[2.0.0,3.0.0)")],
                Artifacts = [new ArtifactFixture()],
            },
            new VersionFixture
            {
                Version = "1.0.0",
                Dependencies = [new DepFixture("y", null)],
                Artifacts = [new ArtifactFixture()],
            });
        second.AddSei("a", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts =
            [
                new ArtifactFixture { Prerequisites = [new DepFixture("z", null)] },
                new ArtifactFixture(),
            ],
        });
        WriteTwoSourceConfig(fixture, first.BaseUrl, second, secondServer.BaseUrl);
        var origin = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one", dependencies: [("o", null)]);
        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        var cache = Path.Combine(fixture.StoreDirectory, ".metadata");
        var firstIndex = new Uri(first.BaseUrl.TrimEnd('/') + "/index.json");
        var secondIndex = new Uri(secondServer.BaseUrl.TrimEnd('/') + "/index.json");
        var keep = new[]
        {
            MetadataCache.PathFor(fixture.StoreDirectory, firstIndex.AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, new Uri(firstIndex, "sei/o.json").AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, secondIndex.AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, new Uri(secondIndex, "sei/a.json").AbsoluteUri),
        }.ToDictionary(path => path, File.ReadAllBytes);
        Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);
        foreach (var (path, bytes) in keep)
        {
            File.WriteAllBytes(path, bytes);
        }

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true));

        Assert.Contains("sei/y.json from source 'first'", error.Message);
    }

    [Fact]
    public void OfflinePrecheck_DoesNotPruneOnAShadowedVersion()
    {
        using var fixture = new Fixture();
        using var first = new TestHttpServer(fixture.SourceDirectory);
        var second = new Fixture();
        using var secondServer = new TestHttpServer(second.SourceDirectory);
        // x is unknown in the first source and 1.0 in the second, so the
        // placeholder-versus-shadow distinction decides this: treating 1.0 as
        // settled would prune a's artifact (prerequisite x [2]) and skip y.
        fixture.AddSei("a", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Prerequisites = [new DepFixture("x", "[2.0.0]")],
                    Dependencies = [new DepFixture("y", null)],
                },
            ],
        });
        second.AddSei("x", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        WriteTwoSourceConfig(fixture, first.BaseUrl, second, secondServer.BaseUrl);
        var origin = fixture.WriteOrigin(
            "hello", "1.0.0", "hello.txt", "one", dependencies: [("x", null), ("a", null)]);
        Assert.Throws<SeikoException>(
            () => new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory));
        var cache = Path.Combine(fixture.StoreDirectory, ".metadata");
        var firstIndex = new Uri(first.BaseUrl.TrimEnd('/') + "/index.json");
        var secondIndex = new Uri(secondServer.BaseUrl.TrimEnd('/') + "/index.json");
        var keep = new[]
        {
            MetadataCache.PathFor(fixture.StoreDirectory, firstIndex.AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, new Uri(firstIndex, "sei/a.json").AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, secondIndex.AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, new Uri(secondIndex, "sei/x.json").AbsoluteUri),
        }.ToDictionary(path => path, File.ReadAllBytes);
        Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);
        foreach (var (path, bytes) in keep)
        {
            File.WriteAllBytes(path, bytes);
        }

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true));

        Assert.Contains("sei/y.json", error.Message);
    }

    [Fact]
    public void OfflinePrecheck_PrunesOnAUnsatisfiablePrerequisite()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("b", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Prerequisites = [new DepFixture("x", "[2.0.0,3.0.0)")],
                    Dependencies = [new DepFixture("y", null)],
                },
            ],
        });
        fixture.AddSei("x", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddSei("y", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        // x is pinned below 2, so the artifact's prerequisite can never hold;
        // y must not become a requirement.
        var origin = fixture.WriteOrigin(
            "hello", "1.0.0", "hello.txt", "one", dependencies: [("x", "[1.0.0,2.0.0)"), ("b", null)]);
        // Online the graph is already unsolvable; only the caches matter here.
        Assert.Throws<SeikoException>(
            () => new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory));
        var cache = Path.Combine(fixture.StoreDirectory, ".metadata");
        var index = new Uri(server.BaseUrl.TrimEnd('/') + "/index.json");
        var keep = new[] { "b", "x" }
            .Select(name => MetadataCache.PathFor(fixture.StoreDirectory, new Uri(index, $"sei/{name}.json").AbsoluteUri))
            .ToDictionary(path => path, File.ReadAllBytes);
        var root = MetadataCache.PathFor(fixture.StoreDirectory, index.AbsoluteUri);
        var savedRoot = File.ReadAllBytes(root);
        Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);
        foreach (var (path, bytes) in keep)
        {
            File.WriteAllBytes(path, bytes);
        }

        File.WriteAllBytes(root, savedRoot);

        // Nothing is left to try, and the real run would say the same: y is
        // not a requirement.
        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true));

        // The artifact is proven dead, so y is no requirement of this run.
        // Nothing is left to try, and that conflict is the whole answer.
        Assert.Equal("resolve", error.Code);
        Assert.DoesNotContain("sei/y.json", error.Message);
    }

    [Fact]
    public void OfflinePrecheck_KeepsScanningPastACorruptCache()
    {
        using var fixture = new Fixture();
        using var first = new TestHttpServer(fixture.SourceDirectory);
        var second = new Fixture();
        using var secondServer = new TestHttpServer(second.SourceDirectory);
        fixture.AddSei("x", new VersionFixture { Version = "9.0.0", Artifacts = [new ArtifactFixture()] });
        second.AddSei("x", new VersionFixture { Version = "9.0.0", Artifacts = [new ArtifactFixture()] });
        WriteTwoSourceConfig(fixture, first.BaseUrl, second, secondServer.BaseUrl);
        // A bare file named x: the disambiguation asks the sources for x. The
        // first source's cache is corrupt, the second is cold. Both are gaps —
        // a corrupt file answers nothing, so the walk must not stop there.
        fixture.WriteOrigin("x", "1.0.0", "x.txt", "one", fileName: "x.sei");
        var working = Directory.GetCurrentDirectory();
        var local = Path.Combine(working, "x.sei");
        File.Copy(Path.Combine(fixture.Root, "dist/x.sei"), local, overwrite: true);
        try
        {
            // Prime the caches online first (this run still reports x as a
            // gap, which is fine); then break the first source's file.
            Assert.Throws<SeikoException>(() => new SeikoClient().AddMany(
                fixture.Root, ["x.sei"], remove: null, storeRoot: fixture.StoreDirectory));
            var firstPath = MetadataCache.PathFor(
                fixture.StoreDirectory, first.BaseUrl.TrimEnd('/') + "/sei/x.json");
            File.WriteAllText(firstPath, "{ broken");
            var x = Assert.Throws<SeikoException>(() => new SeikoClient().AddMany(
                fixture.Root, ["x.sei"], remove: null, storeRoot: fixture.StoreDirectory, dryRun: true, offline: true));

            Assert.Contains("sei/x.json from source 'first' (unreadable)", x.Message);
            Assert.Contains("sei/x.json from source 'second'", x.Message);
            Assert.DoesNotContain("from source 'second' (unreadable)", x.Message);
        }
        finally
        {
            File.Delete(local);
        }
    }

    [Fact]
    public void OfflinePrecheck_StopsCrossCheckingAfterADigestMismatch()
    {
        using var fixture = new Fixture();
        using var first = new TestHttpServer(fixture.SourceDirectory);
        var second = new Fixture();
        using var secondServer = new TestHttpServer(second.SourceDirectory);
        var third = new Fixture();
        using var thirdServer = new TestHttpServer(third.SourceDirectory);
        third.AddSei("x", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        third.WriteConfig("win-x64");
        fixture.AddSei("x", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["x.txt"] = "a" } }],
        });
        second.AddSei("x", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["x.txt"] = "b" } }],
        });
        // The same version with different bytes on the mirror makes the run
        // fail at the second source; a third source is never read.
        WriteTwoSourceConfig(fixture, first.BaseUrl, second, secondServer.BaseUrl);
        var origin = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one", dependencies: [("x", null)]);
        // Online the mirror disagreement already fails; the caches are written.
        Assert.Throws<SeikoException>(
            () => new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory));
        var cache = Path.Combine(fixture.StoreDirectory, ".metadata");
        var firstIndex = new Uri(first.BaseUrl.TrimEnd('/') + "/index.json");
        var secondIndex = new Uri(secondServer.BaseUrl.TrimEnd('/') + "/index.json");
        var keep = new[]
        {
            MetadataCache.PathFor(fixture.StoreDirectory, firstIndex.AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, new Uri(firstIndex, "sei/x.json").AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, secondIndex.AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, new Uri(secondIndex, "sei/x.json").AbsoluteUri),
        }.ToDictionary(path => path, File.ReadAllBytes);
        Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);
        foreach (var (path, bytes) in keep)
        {
            File.WriteAllBytes(path, bytes);
        }

        // A third, cold source is appended: the mismatch is already proven at
        // the second, so the run never reaches the third and its cold cache
        // must not turn into a gap.
        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            File.ReadAllText(Path.Combine(fixture.Root, "seiko.conf")).Replace(
                "\n]\n",
                $"\n  {{ id = \"third\", index = \"{thirdServer.BaseUrl.TrimEnd('/')}/index.json\" }}\n]\n",
                StringComparison.Ordinal));

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true));

        Assert.Equal("checksum", error.Code);
        Assert.DoesNotContain("sei/x.json from source 'third'", error.Message);
    }

    [Fact]
    public void VerifyInfo_StopsAtTheFirstAnswerEvenWhenTheCacheIsBroken()
    {
        using var fixture = new Fixture();
        using var first = new TestHttpServer(fixture.SourceDirectory);
        var second = new Fixture();
        using var secondServer = new TestHttpServer(second.SourceDirectory);
        fixture.AddSei("x", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        WriteTwoSourceConfig(fixture, first.BaseUrl, second, secondServer.BaseUrl);
        var origin = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one");
        var client = new SeikoClient();
        client.Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true);
        // `hello` is answered by the origin, so a cold http source is fine.
        WriteOriginConfig(fixture, "hello", "dist/hello.sei", "selectors = [ { rid = \"*\" } ]");

        var info = client.Info(fixture.Root, "hello", offline: true, storeRoot: fixture.StoreDirectory);

        Assert.Equal("hello", info.Name);
    }

    [Fact]
    public void OfflinePrecheck_FrozenReportsABrokenIndexNotAMissingPackage()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("a", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("a", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var local = Path.Combine(fixture.Root, "local-index");
        Directory.CreateDirectory(Path.Combine(local, "sei"));
        File.WriteAllText(Path.Combine(local, "index.json"), "{\"schemaVersion\":99,\"sei\":[]}");
        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            $"schemaVersion = {SeikoConfig.CurrentSchemaVersion}\n"
            + $"sources = [ {{ id = \"local\", index = \"{local.Replace('\\', '/')}/index.json\" }} ]\n"
            + "rid = \"win-x64\"\nsei = [ { name = \"a\", version = \"[1.0.0]\" } ]\n");
        var lockPath = Path.Combine(fixture.Root, "seiko.lock.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(lockPath))!;
        node["sei"]!.AsArray()[0]!["source"] = "local";
        File.WriteAllText(lockPath, node.ToJsonString());

        var error = Assert.Throws<SeikoException>(() => client.Sync(
            fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen, offline: true));

        Assert.Equal("source", error.Code);
        Assert.Contains("schemaVersion 99", error.Message);
    }

    [Fact]
    public void OfflinePrecheck_KeepsAnArtifactAliveWhenTheConflictLiesOutside()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("b", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Conflicts = [new DepFixture("x", "[9.0.0,10.0.0)")],
                    Dependencies = [new DepFixture("y", null)],
                },
            ],
        });
        fixture.AddSei(
            "x",
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddSei("y", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        // x is unknown and the conflict is far away, so the artifact lives and
        // y is a real requirement. A subtractor that mangles disjoint ranges
        // would call it dead.
        var origin = fixture.WriteOrigin(
            "hello", "1.0.0", "hello.txt", "one", dependencies: [("x", null), ("b", null)]);
        new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        var cache = Path.Combine(fixture.StoreDirectory, ".metadata");
        var index = new Uri(server.BaseUrl.TrimEnd('/') + "/index.json");
        var keep = MetadataCache.PathFor(fixture.StoreDirectory, new Uri(index, "sei/b.json").AbsoluteUri);
        var saved = File.ReadAllBytes(keep);
        var root = MetadataCache.PathFor(fixture.StoreDirectory, index.AbsoluteUri);
        var savedRoot = File.ReadAllBytes(root);
        Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(keep, saved);
        File.WriteAllBytes(root, savedRoot);

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true));

        Assert.Contains("sei/y.json", error.Message);
    }

    [Fact]
    public void OfflinePrecheck_SavesTheInitialRangeOfAShadowedAnswer()
    {
        using var fixture = new Fixture();
        using var first = new TestHttpServer(fixture.SourceDirectory);
        var second = new Fixture();
        using var secondServer = new TestHttpServer(second.SourceDirectory);
        // x is unknown in the first source and 1.0 in the second; the root
        // pins x below 2, and a's artifact needs x at 2 or above. Both known
        // ranges already contradict, so the artifact is dead and y must not
        // be read. Only the initial range saved makes that visible.
        fixture.AddSei("a", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Prerequisites = [new DepFixture("x", "[2.0.0,3.0.0)")],
                    Dependencies = [new DepFixture("y", null)],
                },
            ],
        });
        fixture.AddSei("y", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        second.AddSei("x", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        WriteTwoSourceConfig(fixture, first.BaseUrl, second, secondServer.BaseUrl);
        var origin = fixture.WriteOrigin(
            "hello", "1.0.0", "hello.txt", "one", dependencies: [("x", "[1.0.0,2.0.0)"), ("a", null)]);
        Assert.Throws<SeikoException>(
            () => new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory));
        var cache = Path.Combine(fixture.StoreDirectory, ".metadata");
        var firstIndex = new Uri(first.BaseUrl.TrimEnd('/') + "/index.json");
        var secondIndex = new Uri(secondServer.BaseUrl.TrimEnd('/') + "/index.json");
        var keep = new[]
        {
            MetadataCache.PathFor(fixture.StoreDirectory, firstIndex.AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, new Uri(firstIndex, "sei/a.json").AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, secondIndex.AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, new Uri(secondIndex, "sei/x.json").AbsoluteUri),
        }.ToDictionary(path => path, File.ReadAllBytes);
        Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);
        foreach (var (path, bytes) in keep)
        {
            File.WriteAllBytes(path, bytes);
        }

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true));

        Assert.DoesNotContain("sei/y.json", error.Message);
    }

    [Fact]
    public void OfflinePrecheck_DoesNotFailTheWholeRunOnAProbedArtifactsChecksum()
    {
        using var fixture = new Fixture();
        using var first = new TestHttpServer(fixture.SourceDirectory);
        var second = new Fixture();
        using var secondServer = new TestHttpServer(second.SourceDirectory);
        // Everything cached. a0 is more specific and is what the run picks;
        // only the mirror disagrees on a1, which the ambiguity probe visits.
        // That must not turn a success into a failure.
        fixture.AddSei("p", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["p.txt"] = "p" } }] });
        fixture.AddSei("a", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture { Prerequisites = [new DepFixture("p", null)] },
                new ArtifactFixture(),
            ],
        });
        second.AddSei("p", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["p.txt"] = "p" } }] });
        second.AddSei("a", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture { Prerequisites = [new DepFixture("p", null)] },
                new ArtifactFixture(),
            ],
        });
        WriteTwoSourceConfig(fixture, first.BaseUrl, second, secondServer.BaseUrl);
        var origin = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one", dependencies: [("p", null), ("a", null)]);
        var client = new SeikoClient();
        client.Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        // Make only the mirror's a1 disagree: same conditions, other bytes.
        var indexPath = Path.Combine(second.SourceDirectory, "sei", "a.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(indexPath))!;
        node["versions"]![0]!["artifacts"]![1]!["source"]!["sha256"] = new string('b', 64);
        File.WriteAllText(indexPath, node.ToJsonString());
        // Re-fetch the mirror's metadata so the cache holds the disagreement.
        File.Delete(MetadataCache.PathFor(
            fixture.StoreDirectory, secondServer.BaseUrl.TrimEnd('/') + "/sei/a.json"));
        // Prime the mirror's a again without failing the test either way.
        try
        {
            client.Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        }
        catch (SeikoException)
        {
            // The mismatch itself is not what this test is about.
        }

        var lockPath = Path.Combine(fixture.Root, "seiko.lock.json");
        var lockNode = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(lockPath))!;
        lockNode["sei"]!.AsArray().Single(item => item!["name"]!.GetValue<string>() == "a")!["artifact"]!["sha256"] =
            node["versions"]![0]!["artifacts"]![0]!["source"]!["sha256"]!.GetValue<string>();
        File.WriteAllText(lockPath, lockNode.ToJsonString());

        // Every metadata file is cached now; the pre-check must accept.
        var result = client.Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Locked, offline: true);

        Assert.Equal(3, result.SeiInstalled);
    }

    [Fact]
    public void OfflinePrecheck_StopsOnTheSameSourceOrderAsTheRun()
    {
        using var fixture = new Fixture();
        using var first = new TestHttpServer(fixture.SourceDirectory);
        var second = new Fixture();
        using var secondServer = new TestHttpServer(second.SourceDirectory);
        // Root order z, a. The run checks z first, hits the mirror's mismatch
        // there and never reads the mirror's a. Sorted order would read a
        // first and turn it into a gap.
        fixture.AddSei("z", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["z.txt"] = "z" } }] });
        fixture.AddSei("a", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["a.txt"] = "a" } }] });
        second.AddSei("z", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["z.txt"] = "z" } }] });
        second.AddSei("a", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["a.txt"] = "a" } }] });
        WriteTwoSourceConfig(fixture, first.BaseUrl, second, secondServer.BaseUrl);
        var origin = fixture.WriteOrigin(
            "hello", "1.0.0", "hello.txt", "one", dependencies: [("z", null), ("a", null)]);
        var client = new SeikoClient();
        client.Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        // The mirror's cached z gets a different digest than the first
        // source's; its a goes cold, so reading it would show up as a gap.
        var zPath = MetadataCache.PathFor(
            fixture.StoreDirectory, secondServer.BaseUrl.TrimEnd('/') + "/sei/z.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(zPath))!;
        node["versions"]![0]!["artifacts"]![0]!["source"]!["sha256"] = new string('c', 64);
        File.WriteAllText(zPath, node.ToJsonString());
        File.Delete(MetadataCache.PathFor(
            fixture.StoreDirectory, secondServer.BaseUrl.TrimEnd('/') + "/sei/a.json"));

        var error = Assert.Throws<SeikoException>(() => client.Add(
            fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true));

        Assert.Equal("checksum", error.Code);
        Assert.DoesNotContain("sei/a.json from source 'second'", error.Message);
    }

    [Fact]
    public void OfflinePrecheck_KeepsTheInitialRangeAcrossAnArtifactBacktrack()
    {
        using var fixture = new Fixture();
        using var first = new TestHttpServer(fixture.SourceDirectory);
        var second = new Fixture();
        using var secondServer = new TestHttpServer(second.SourceDirectory);
        // x is unknown in the first source and only present in the second.
        // x 1.0 has two artifacts: the first needs absent, which is a recorded
        // 404; the second needs y, whose metadata pins x at 2 or above and is
        // cached. That contradicts the root's [1,2), so the root range has to
        // survive the first artifact's failure for the second one to be
        // pruned before z is read.
        fixture.AddSei("y", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("x", "[2.0.0,3.0.0)"), new DepFixture("z", null)],
            Artifacts = [new ArtifactFixture()],
        });
        fixture.AddSei("z", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        second.AddSei("x", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture { Dependencies = [new DepFixture("absent", null)] },
                new ArtifactFixture { Dependencies = [new DepFixture("y", null)] },
            ],
        });
        WriteTwoSourceConfig(fixture, first.BaseUrl, second, secondServer.BaseUrl);
        var origin = fixture.WriteOrigin(
            "hello", "1.0.0", "hello.txt", "one", dependencies: [("x", "[1.0.0,2.0.0)")]);
        try
        {
            // Online the graph fails on the root range; the metadata does not.
            new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        }
        catch (SeikoException)
        {
        }

        var cache = Path.Combine(fixture.StoreDirectory, ".metadata");
        var firstIndex = new Uri(first.BaseUrl.TrimEnd('/') + "/index.json");
        var secondIndex = new Uri(secondServer.BaseUrl.TrimEnd('/') + "/index.json");
        var keep = new[]
        {
            MetadataCache.PathFor(fixture.StoreDirectory, firstIndex.AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, secondIndex.AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, new Uri(secondIndex, "sei/x.json").AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, new Uri(firstIndex, "sei/y.json").AbsoluteUri),
        }.Where(File.Exists).ToDictionary(path => path, File.ReadAllBytes);
        Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);
        foreach (var (path, bytes) in keep)
        {
            File.WriteAllBytes(path, bytes);
        }

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true));

        // y came from a cache, so its x [2,3) met the root [1,2) and the
        // second artifact is dead; only y's own metadata and x were missing.
        // (The parent passes this one too; the deeper variant below is the
        // discriminating case.)
        Assert.DoesNotContain("sei/z.json", error.Message);
    }

    // The two artifacts are equally specific, so the run reports an ambiguity
    // either way; this case guards the state the backtrack leaves behind.
    [Fact]
    public void OfflinePrecheck_DoesNotCallAFailedArtifactSolvable()
    {
        using var fixture = new Fixture();
        using var first = new TestHttpServer(fixture.SourceDirectory);
        var second = new Fixture();
        using var secondServer = new TestHttpServer(second.SourceDirectory);
        // Two artifacts with different prerequisites, both specificity 1. Only
        // a1 disagrees with the mirror, so the run takes a0 and succeeds; a
        // probe that swallows the checksum would call a1 solvable and report
        // a false ambiguity.
        fixture.AddSei("p", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["p.txt"] = "p" } }] });
        fixture.AddSei("q", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["q.txt"] = "q" } }] });
        fixture.AddSei("a", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture { Prerequisites = [new DepFixture("p", null)] },
                new ArtifactFixture { Prerequisites = [new DepFixture("q", null)] },
            ],
        });
        second.AddSei("p", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["p.txt"] = "p" } }] });
        second.AddSei("q", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["q.txt"] = "q" } }] });
        second.AddSei("a", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture { Prerequisites = [new DepFixture("p", null)] },
                new ArtifactFixture { Prerequisites = [new DepFixture("q", null)] },
            ],
        });
        WriteTwoSourceConfig(fixture, first.BaseUrl, second, secondServer.BaseUrl);
        fixture.AddSei("hello", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        var origin = fixture.WriteOrigin(
            "hello", "1.0.0", "hello.txt", "one", dependencies: [("p", null), ("q", null), ("a", null)]);
        var client = new SeikoClient();
        try
        {
            client.Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        }
        catch (SeikoException)
        {
            // Priming only; this graph is ambiguous by design.
        }

        // The mirror's a1 gets other bytes; a1's row only.
        var mirrorPath = Path.Combine(second.SourceDirectory, "sei", "a.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(mirrorPath))!;
        node["versions"]![0]!["artifacts"]![1]!["source"]!["sha256"] = new string('d', 64);
        File.WriteAllText(mirrorPath, node.ToJsonString());
        File.Delete(MetadataCache.PathFor(
            fixture.StoreDirectory, secondServer.BaseUrl.TrimEnd('/') + "/sei/a.json"));

        // The run itself reports the ambiguity; only the probe's own dry
        // resolution must not — a failed probe verdict would surface first as
        // `resolve` without the gaps.
        // Every metadata read is a cache hit now, so no gap can rescue the
        // probe: if it swallowed the checksum it would call both artifacts
        // solvable and report an ambiguity (the mirror's a is refilled by a
        // priming read here first).
        try
        {
            client.Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        }
        catch (SeikoException)
        {
        }

        File.Delete(MetadataCache.PathFor(
            fixture.StoreDirectory, secondServer.BaseUrl.TrimEnd('/') + "/sei/a.json"));
        try
        {
            client.Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        }
        catch (SeikoException)
        {
        }

        client.Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true);

        // a0 is the artifact both sides can prove solvable; a1 was rightly
        // called unsolvable rather than becoming a second solvable choice.
        var locked = Assert.Single(client.ReadLock(fixture.Root)!.Sei, item => item.Name == "a");
        Assert.Contains(locked.Artifact.Prerequisites, item => item.Name == "p");
        Assert.DoesNotContain(locked.Artifact.Prerequisites, item => item.Name == "q");
    }

    [Fact]
    public void OfflinePrecheck_KeepsARealSourceFailureAfterAGap()
    {
        using var fixture = new Fixture();
        using var first = new TestHttpServer(fixture.SourceDirectory);
        var second = new Fixture();
        using var secondServer = new TestHttpServer(second.SourceDirectory);
        var third = Path.Combine(fixture.Root, "broken-index");
        Directory.CreateDirectory(Path.Combine(third, "sei"));
        File.WriteAllText(Path.Combine(third, "index.json"), "{\"schemaVersion\":99,\"sei\":[]}");
        File.WriteAllText(Path.Combine(third, "sei", "x.json"), "{\"schemaVersion\":2,\"name\":\"x\",\"versions\":[]}");
        fixture.AddSei("x", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        second.AddSei("x", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        WriteTwoSourceConfig(fixture, first.BaseUrl, second, secondServer.BaseUrl);
        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            File.ReadAllText(Path.Combine(fixture.Root, "seiko.conf")).Replace(
                "\n]\n",
                $"\n  {{ id = \"third\", index = \"{third.Replace('\\', '/')}/index.json\" }}\n]\n",
                StringComparison.Ordinal));
        var origin = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one", dependencies: [("x", null)]);
        try
        {
            // The broken third source already fails the online run.
            new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        }
        catch (SeikoException)
        {
        }

        var cache = Path.Combine(fixture.StoreDirectory, ".metadata");
        var firstIndex = new Uri(first.BaseUrl.TrimEnd('/') + "/index.json");
        var secondIndex = new Uri(secondServer.BaseUrl.TrimEnd('/') + "/index.json");
        var keep = new[]
        {
            MetadataCache.PathFor(fixture.StoreDirectory, firstIndex.AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, new Uri(firstIndex, "sei/x.json").AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, secondIndex.AbsoluteUri),
        }.Where(File.Exists).ToDictionary(path => path, File.ReadAllBytes);
        Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);
        foreach (var (path, bytes) in keep)
        {
            File.WriteAllBytes(path, bytes);
        }

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true));

        // Both survive: the gap read first, and the broken third source.
        Assert.Contains("sei/x.json from source 'second'", error.Message);
        Assert.Contains("schemaVersion 99", error.Message);
    }

    [Fact]
    public void OfflinePrecheck_FrozenBindsTheLockedOriginPath()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        // The lock pins p1; the declaration now says p2. Only the lock's own
        // snapshot still exists, so a frozen run must follow the lock.
        var one = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one", fileName: "hello-a.sei");
        var client = new SeikoClient();
        client.Add(fixture.Root, Rel(fixture, one), storeRoot: fixture.StoreDirectory);
        fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "two", fileName: "hello-b.sei");
        WriteOriginConfig(fixture, "hello", "dist/hello-b.sei", "selectors = [ { rid = \"*\" } ]");
        // Only the lock's own snapshot survives: the original file is gone
        // and the newer file was never snapshotted under the lock's digest.
        File.Delete(Path.Combine(fixture.Root, one));
        File.Delete(Path.Combine(fixture.Root, "dist/hello.sei"));
        var snapshot = Assert.Single(Directory.GetFiles(Path.Combine(fixture.StoreDirectory, "origin")));
        Assert.StartsWith("hello", Path.GetFileName(snapshot), StringComparison.Ordinal);

        var result = client.Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen, offline: true);

        Assert.Equal(1, result.SeiInstalled);
    }

    [Fact]
    public void OfflinePrecheck_KeepsSourceFailureAfterAGapInASeiFile()
    {
        using var fixture = new Fixture();
        using var first = new TestHttpServer(fixture.SourceDirectory);
        var second = new Fixture();
        using var secondServer = new TestHttpServer(second.SourceDirectory);
        var third = Path.Combine(fixture.Root, "half-broken");
        Directory.CreateDirectory(Path.Combine(third, "sei"));
        // The third root opens, but its sei file for x carries an unsupported
        // schema; only the cross-source read of it fails.
        File.WriteAllText(Path.Combine(third, "index.json"), "{\"schemaVersion\":2,\"sei\":[]}");
        File.WriteAllText(
            Path.Combine(third, "sei", "x.json"),
            "{\"schemaVersion\":99,\"name\":\"x\",\"versions\":[]}");
        fixture.AddSei("x", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        second.AddSei("x", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        WriteTwoSourceConfig(fixture, first.BaseUrl, second, secondServer.BaseUrl);
        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            File.ReadAllText(Path.Combine(fixture.Root, "seiko.conf")).Replace(
                "\n]\n",
                $"\n  {{ id = \"third\", index = \"{third.Replace('\\', '/')}/index.json\" }}\n]\n",
                StringComparison.Ordinal));
        var origin = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one", dependencies: [("x", null)]);
        try
        {
            new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        }
        catch (SeikoException)
        {
        }

        var cache = Path.Combine(fixture.StoreDirectory, ".metadata");
        var firstIndex = new Uri(first.BaseUrl.TrimEnd('/') + "/index.json");
        var secondIndex = new Uri(secondServer.BaseUrl.TrimEnd('/') + "/index.json");
        var keep = new[]
        {
            MetadataCache.PathFor(fixture.StoreDirectory, firstIndex.AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, new Uri(firstIndex, "sei/x.json").AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, secondIndex.AbsoluteUri),
        }.Where(File.Exists).ToDictionary(path => path, File.ReadAllBytes);
        Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);
        foreach (var (path, bytes) in keep)
        {
            File.WriteAllBytes(path, bytes);
        }

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true));

        // The gap read first and the later real failure both survive.
        Assert.Contains("sei/x.json from source 'second'", error.Message);
        Assert.Contains("schemaVersion 99", error.Message);
    }

    [Fact]
    public void OfflinePrecheck_SourceFailureOutranksAResolveVerdict()
    {
        using var fixture = new Fixture();
        using var first = new TestHttpServer(fixture.SourceDirectory);
        var broken = Path.Combine(fixture.Root, "broken");
        Directory.CreateDirectory(broken);
        // The only source cannot be built; the graph itself would just say
        // "not found". The run fails on construction first.
        File.WriteAllText(Path.Combine(broken, "index.json"), "{\"schemaVersion\":99,\"sei\":[]}");
        fixture.WriteHttpConfig(first.BaseUrl, "win-x64");
        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            $"schemaVersion = {SeikoConfig.CurrentSchemaVersion}\n"
            + $"sources = [ {{ id = \"broken\", index = \"{broken.Replace('\\', '/')}/index.json\" }} ]\n"
            + "rid = \"win-x64\"\nsei = []\n");
        var origin = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one", dependencies: [("a", null)]);

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true));

        Assert.Equal("source", error.Code);
        Assert.Contains("schemaVersion 99", error.Message);
    }

    [Fact]
    public void OfflinePrecheck_FrozenReportsAConstructionFailureItSucceededPast()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        var broken = Path.Combine(fixture.Root, "broken");
        Directory.CreateDirectory(broken);
        fixture.AddSei("a", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("a", "[1.0.0]"));
        File.WriteAllText(Path.Combine(broken, "index.json"), "{\"schemaVersion\":99,\"sei\":[]}");
        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            $"schemaVersion = {SeikoConfig.CurrentSchemaVersion}\n"
            + $"sources = [ {{ id = \"http\", index = \"{server.BaseUrl}index.json\" }}, "
            + $"{{ id = \"broken\", index = \"{broken.Replace('\\', '/')}/index.json\" }} ]\n"
            + "rid = \"win-x64\"\nsei = [ { name = \"a\", version = \"[1.0.0]\" } ]\n");
        var client = new SeikoClient();
        // The priming sync already trips over the broken source.
        try
        {
            client.Sync(fixture.Root, fixture.StoreDirectory);
        }
        catch (SeikoException)
        {
        }

        var error = Assert.Throws<SeikoException>(() => client.Sync(
            fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen, offline: true));

        // The replay itself would succeed; construction of the second source
        // fails first in the real run.
        Assert.Equal("source", error.Code);
        Assert.Contains("schemaVersion 99", error.Message);
    }

    [Fact]
    public void OfflinePrecheck_KeepsTheInitialRangeWhenADeeperVersionRetries()
    {
        using var fixture = new Fixture();
        using var first = new TestHttpServer(fixture.SourceDirectory);
        var second = new Fixture();
        using var secondServer = new TestHttpServer(second.SourceDirectory);
        // Same shape one level deeper: the polluted range of y 2.0's failed
        // artifact must not survive into y 1.0, or its cold package is never
        // read.
        fixture.AddSei("absent", new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddSei(
            "y",
            new VersionFixture
            {
                Version = "2.0.0",
                Artifacts =
                [
                    new ArtifactFixture { Dependencies = [new DepFixture("absent", null)] },
                    new ArtifactFixture { Dependencies = [new DepFixture("x", "[2.0.0,3.0.0)"), new DepFixture("absent", null)] },
                ],
            },
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts = [new ArtifactFixture { Dependencies = [new DepFixture("x", "[3.0.0,4.0.0)"), new DepFixture("cold", null)] }],
            });
        fixture.AddSei("cold", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        second.AddSei("x", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture { Dependencies = [new DepFixture("absent", null)] },
                new ArtifactFixture { Dependencies = [new DepFixture("y", null)] },
            ],
        });
        WriteTwoSourceConfig(fixture, first.BaseUrl, second, secondServer.BaseUrl);
        var origin = fixture.WriteOrigin(
            "hello", "1.0.0", "hello.txt", "one", dependencies: [("x", "[1.0.0,4.0.0)")]);
        try
        {
            new SeikoClient().Add(fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory);
        }
        catch (SeikoException)
        {
        }

        var cache = Path.Combine(fixture.StoreDirectory, ".metadata");
        var firstIndex = new Uri(first.BaseUrl.TrimEnd('/') + "/index.json");
        var secondIndex = new Uri(secondServer.BaseUrl.TrimEnd('/') + "/index.json");
        var keep = new[]
        {
            MetadataCache.PathFor(fixture.StoreDirectory, firstIndex.AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, secondIndex.AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, new Uri(secondIndex, "sei/x.json").AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, new Uri(firstIndex, "sei/absent.json").AbsoluteUri),
            MetadataCache.PathFor(fixture.StoreDirectory, new Uri(firstIndex, "sei/y.json").AbsoluteUri),
        }.Where(File.Exists).ToDictionary(path => path, File.ReadAllBytes);
        Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);
        foreach (var (path, bytes) in keep)
        {
            File.WriteAllBytes(path, bytes);
        }

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root, Rel(fixture, origin), storeRoot: fixture.StoreDirectory, offline: true));

        Assert.Contains("sei/cold.json", error.Message);
    }

    [Fact]
    public void SameFile_FoldsCaseOnlyWhereTheVolumeDoes()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "dist"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, "Dist"));
        File.WriteAllText(Path.Combine(fixture.Root, "dist", "hello.sei"), "one");
        File.WriteAllText(Path.Combine(fixture.Root, "Dist", "hello.sei"), "two");

        Assert.True(OriginPaths.SameFile("dist/./hello.sei", "dist/x/../hello.sei", fixture.Root));
        // On a case-insensitive volume the two directories are one and the
        // probe spelling exists; on a case-sensitive one they are distinct and
        // must stay distinct.
        var insensitive = File.Exists(Path.Combine(fixture.Root, "dist", "HELLO.SEI"));
        Assert.Equal(insensitive, OriginPaths.SameFile("dist/hello.sei", "Dist/hello.sei", fixture.Root));
    }

    [Fact]
    public void Add_KeepsAnInternalDotDotInTheConfigInsteadOfDuplicatingThePath()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var origin = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one");
        WriteOriginConfig(
            fixture,
            "hello",
            "\"dist/../dist/hello.sei\"",
            "selectors = [ { rid = \"*\" } ]");

        // The same file by another spelling; the declared form is the one the
        // config chose, so it must survive unchanged.
        new SeikoClient().Add(
            fixture.Root,
            "./" + Rel(fixture, origin),
            storeRoot: fixture.StoreDirectory);

        var paths = Assert.Single(ConfigLoader.Load(fixture.Root).Origin).Paths;
        Assert.Equal("dist/../dist/hello.sei", Assert.Single(paths));
    }

    [Fact]
    public void Add_DryRunJsonMarksAnOriginChangeAsNotIndexVerified()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var origin = fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one");

        var (stdout, stderr, exitCode) = Cli.Run(
        [
            "add", Rel(fixture, origin),
            "--root", fixture.Root,
            "--store", fixture.StoreDirectory,
            "--dry-run", "--json",
        ]);

        Assert.Equal(0, exitCode);
        var change = System.Text.Json.Nodes.JsonNode.Parse(stdout)!["data"]!["changes"]![0]!;
        Assert.Equal("add", change["action"]!.GetValue<string>());
        Assert.Equal("origin", change["source"]!.GetValue<string>());
        Assert.False(change["indexVerified"]!.GetValue<bool>());
        // Progress and diagnostics stay off stdout.
        Assert.DoesNotContain("{", stderr);
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
            + "sei = [ { name = \"x\", version = \"[1.0.0]\" }, { name = \"gone\", version = \"[1.0.0]\" }, { name = \"hello\" } ]\n");

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

    // The gap entries of an offline summary, in order, without the prose. A
    // failure-carrying message wraps the list in parentheses, and the search
    // limit note may follow it.
    private static string[] Gaps(string message)
    {
        // The list ends where the message ends its job: the final sentence
        // period, or — in a Combined message — the wrapping parenthesis. A
        // search-limit note is a following sentence, not an entry.
        var list = message
            .Split("missing cached metadata: ", StringSplitOptions.None)[1];
        var note = list.IndexOf("; the probe hit", StringComparison.Ordinal);
        if (note >= 0)
        {
            list = list[..note];
        }

        list = list.TrimEnd(')', ' ');
        // The final sentence period is the last ". " in the list; a source id
        // may itself contain ". ".
        var end = list.LastIndexOf(". ", StringComparison.Ordinal);
        if (end >= 0)
        {
            list = list[..end];
        }

        return list.Split("; ", StringSplitOptions.None);
    }

    // Two http sources in order; each fixture serves its own directory.
    private static void WriteTwoSourceConfig(Fixture fixture, string firstUrl, Fixture second, string secondUrl)
    {
        fixture.WriteHttpConfig(firstUrl, "win-x64");
        second.WriteConfig("win-x64");
        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            $"schemaVersion = {SeikoConfig.CurrentSchemaVersion}\n"
            + "sources = [\n"
            + $"  {{ id = \"first\", index = \"{firstUrl.TrimEnd('/')}/index.json\" }}\n"
            + $"  {{ id = \"second\", index = \"{secondUrl.TrimEnd('/')}/index.json\" }}\n"
            + "]\n"
            + "rid = \"win-x64\"\n"
            + "sei = []\n");
    }

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
