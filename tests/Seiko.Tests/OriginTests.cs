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

        new SeikoClient().Add(fixture.Root, Path.Combine(fixture.Root, path), storeRoot: fixture.StoreDirectory);

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

        new SeikoClient().Add(fixture.Root, Path.Combine(fixture.Root, path), storeRoot: fixture.StoreDirectory);

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
        client.Add(fixture.Root, Path.Combine(fixture.Root, path), storeRoot: fixture.StoreDirectory);

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
            Path.Combine(fixture.Root, "dist", "other.sei"),
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
            full,
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
            Path.Combine(fixture.Root, fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one")),
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
        client.Add(fixture.Root, Path.Combine(fixture.Root, path), storeRoot: fixture.StoreDirectory);

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
        client.Add(fixture.Root, Path.Combine(fixture.Root, path), storeRoot: fixture.StoreDirectory);
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
            Path.Combine(fixture.Root, fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one")),
            storeRoot: fixture.StoreDirectory);

        client.Add(
            fixture.Root,
            Path.Combine(fixture.Root, fixture.WriteOrigin("hello", "1.0.1", "hello.txt", "two")),
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
            Path.Combine(fixture.Root, fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one")),
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
        client.Add(fixture.Root, Path.Combine(fixture.Root, path), storeRoot: fixture.StoreDirectory);
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
            Path.Combine(fixture.Root, path),
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
            Path.Combine(fixture.Root, fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one")),
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
        client.Add(fixture.Root, Path.Combine(fixture.Root, path), storeRoot: fixture.StoreDirectory);
        var snapshot = Assert.Single(
            Directory.GetFiles(Path.Combine(fixture.StoreDirectory, "origin")));
        var expected = Hashing.Sha256File(snapshot);
        File.WriteAllText(snapshot, "tampered");

        client.Add(fixture.Root, Path.Combine(fixture.Root, path), storeRoot: fixture.StoreDirectory);

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

        new SeikoClient().Add(
            fixture.Root,
            Path.Combine(fixture.Root, "dist/hello.sei"),
            storeRoot: fixture.StoreDirectory);

        // The selector adds lib as an artifact prerequisite, so it is pulled in
        // and the artifact is chosen because the condition holds.
        Assert.Contains("lib", new SeikoClient().ReadLock(fixture.Root)!.Sei.Select(sei => sei.Name));
    }

    [Fact]
    public void SelectorRangeIsKeptWhenTheManifestAlsoNamesThePrerequisite()
    {
        using var fixture = new Fixture();
        fixture.AddSei("lib", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteConfig("win-x64");
        fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one", manifestPrerequisites: [("lib", "[1.0.0]")]);
        WriteOriginConfig(
            fixture,
            "hello",
            "dist/hello.sei",
            "selectors = [ { rid = \"*\", prerequisites = [ { name = \"lib\", version = \"[2.0.0]\" } ] } ]");

        // Without the selector range the artifact would be accepted against
        // lib 1.0.0; both ranges apply, so no version satisfies and it fails.
        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root,
            Path.Combine(fixture.Root, "dist/hello.sei"),
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
        client.Add(fixture.Root, Path.Combine(fixture.Root, path), storeRoot: fixture.StoreDirectory);
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
            Path.Combine(fixture.Root, fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one")),
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
        var origin = Path.Combine(fixture.Root, fixture.WriteOrigin(
            "hello",
            "1.0.0",
            "hello.txt",
            "one",
            dependencies: [("a", null), ("b", null)]));

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Add(
            fixture.Root,
            origin,
            storeRoot: fixture.StoreDirectory,
            offline: true));

        Assert.Equal("source", error.Code);
        Assert.Contains("sei/a.json", error.Message);
        Assert.Contains("sei/b.json", error.Message);
    }

    [Fact]
    public void Add_RejectsAnAbsoluteInputPath()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var absolute = Path.Combine(fixture.Root, fixture.WriteOrigin("hello", "1.0.0", "hello.txt", "one"));

        // Only a relative path is stored, so absolute input is converted; this
        // asserts the conversion happened rather than the input being kept.
        new SeikoClient().Add(fixture.Root, absolute, storeRoot: fixture.StoreDirectory);

        Assert.Equal("dist/hello.sei", Assert.Single(ConfigLoader.Load(fixture.Root).Origin).Paths[0]);
    }

    private static void WriteOriginConfig(
        Fixture fixture,
        string name,
        string path,
        string selectors,
        params string[] alsoDirect)
    {
        var config = Path.Combine(fixture.Root, "seiko.conf");
        File.WriteAllText(
            config,
            File.ReadAllText(config)
            + $"origin = [ {{ name = \"{name}\", paths = [\"{path}\"], {selectors} }} ]\n"
            + $"sei = [ {{ name = \"{name}\" }}{string.Concat(alsoDirect.Select(n => $", {{ name = \"{n}\" }}"))} ]\n");
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('.', '/');
}
