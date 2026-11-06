using Xunit;

namespace Seiko.Tests;

public sealed class WorkflowTests
{
    [Fact]
    public void Why_ReturnsDependencyChain()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.AddSei("app", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("hello", "*")],
            Artifacts = [new ArtifactFixture { Files = { ["app.txt"] = "app" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("app", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        var chains = new SeikoClient().Why(fixture.Root, "hello");

        var chain = Assert.Single(chains);
        Assert.Equal(["app", "hello"], chain);
    }

    [Fact]
    public void Why_ExplainsAPrerequisiteEdge()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.AddSei("app", new VersionFixture
        {
            Version = "1.0.0",
            Prerequisites = [new DepFixture("hello", "*")],
            Artifacts = [new ArtifactFixture { Files = { ["app.txt"] = "app" } }],
        });
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("app", "[1.0.0]"),
            new SpecFixture("hello", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        var chains = new SeikoClient().WhyDetailed(fixture.Root, "hello");

        var chain = Assert.Single(chains, chain => chain.Count > 1);
        Assert.Equal(["app", "hello"], chain.Select(link => link.Name));
        Assert.Equal(["direct", "prerequisite"], chain.Select(link => link.Kind));
    }

    [Fact]
    public void UpdateLatestUsesTheSameFirstSourceAsResolution()
    {
        using var first = new Fixture();
        first.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "one" } }],
        });
        using var second = new Fixture();
        second.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "two" } }],
        });
        first.WriteConfig("win-x64", new SpecFixture("hello", "*"));
        second.WriteConfig("win-x64", new SpecFixture("hello", "*"));
        var firstIndex = Path.Combine(first.SourceDirectory, "index.json").Replace('\\', '/');
        var secondIndex = Path.Combine(second.SourceDirectory, "index.json").Replace('\\', '/');
        File.WriteAllText(
            Path.Combine(first.Root, "seiko.conf"),
            $$"""
            schemaVersion = {{SeikoConfig.CurrentSchemaVersion}}
            sources = [
              { id = "first", index = "{{firstIndex}}" },
              { id = "second", index = "{{secondIndex}}" }
            ]
            rid = "win-x64"
            sei = [
              { name = "hello", version = "*" }
            ]
            """);
        var client = new SeikoClient();
        client.Sync(first.Root, first.StoreDirectory);

        client.Update(first.Root, latest: true, storeRoot: first.StoreDirectory);

        Assert.Equal("[1.0.0]", Assert.Single(ConfigLoader.Load(first.Root).Sei).Version);
        Assert.Equal("1.0.0", Assert.Single(client.ReadLock(first.Root)!.Sei).Version);
    }

    [Fact]
    public void UpdateLatestIsRejectedWithLatestInstallable()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "one" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "*"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var configPath = Path.Combine(fixture.Root, "seiko.conf");
        var before = File.ReadAllText(configPath);

        var error = Assert.Throws<SeikoException>(
            () => client.Update(
                fixture.Root,
                latest: true,
                storeRoot: fixture.StoreDirectory,
                latestInstallable: true));

        Assert.Equal("usage", error.Code);
        Assert.Equal(before, File.ReadAllText(configPath));
    }

    [Fact]
    public void Add_DoesNotEditConfigWhenTheRootLockIsHeld()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64");
        using var held = ProcessLock.Acquire(
            Path.Combine(fixture.Root, ".seiko", "install.lock"),
            "test install root");

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Add(fixture.Root, "hello", fixture.StoreDirectory));

        Assert.Equal("lock", error.Code);
        Assert.Empty(ConfigLoader.Load(fixture.Root).Sei);
    }

    [Fact]
    public void Sync_CancelledBeforeDeployLeavesRootUntouched()
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
        fixture.WriteConfig("win-x64");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => client.Sync(
                fixture.Root,
                fixture.StoreDirectory,
                cancellationToken: cancelled.Token));

        Assert.True(File.Exists(Path.Combine(fixture.Root, "hello.txt")));
        Assert.Single(client.ReadLock(fixture.Root)!.Sei);
    }

    [Fact]
    public void Outdated_ReportsLatestInRangeAndOverall()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "one" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "two" } }],
        });

        var config = ConfigLoader.Load(fixture.Root);
        Assert.Equal("[1.0.0]", Assert.Single(config.Sei).Version);
        var item = Assert.Single(new SeikoClient().Outdated(fixture.Root));

        Assert.Equal("1.0.0", item.Current);
        Assert.Equal("1.0.0", item.LatestInRange);
        Assert.Equal("2.0.0", item.Latest);
        Assert.DoesNotContain(item.Blockers, blocker => blocker.Relation == "rid");
    }

    [Fact]
    public void Outdated_ReportsTheDeclaredRangeAsABlocker()
    {
        using var fixture = new Fixture();
        fixture.AddSei("app", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("dep", "[1.0.0]")],
            Artifacts = [new ArtifactFixture { Files = { ["app.txt"] = "app" } }],
        });
        fixture.AddSei("dep", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["dep.txt"] = "dep" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("app", "[1.0.0]"), new SpecFixture("dep", "[1.0.0]"));
        fixture.AddSei("app", new VersionFixture
        {
            Version = "2.0.0",
            Dependencies = [new DepFixture("dep", "[1.0.0]")],
            Artifacts = [new ArtifactFixture { Files = { ["app.txt"] = "two" } }],
        });
        fixture.AddSei("dep", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["dep.txt"] = "two" } }],
        });
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var item = Assert.Single(client.Outdated(fixture.Root), entry => entry.Name == "dep");

        Assert.Contains(item.Blockers, blocker =>
            blocker.Relation == "declaration" && blocker.Target == "dep" && blocker.Range == "[1.0.0]");
        Assert.Contains(item.Blockers, blocker =>
            blocker.Relation == "dependency" && blocker.Target == "app");
    }

    [Fact]
    public void Outdated_RecomputesInstallableAgainstTheCandidateGraph()
    {
        using var fixture = new Fixture();
        fixture.AddSei("app", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["app.txt"] = "one" } }],
        });
        fixture.AddSei("marker", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["marker.txt"] = "marker" } }],
        });
        fixture.AddSei("app", new VersionFixture
        {
            Version = "2.0.0",
            Dependencies = [new DepFixture("marker", "*")],
            Artifacts =
            [
                new ArtifactFixture
                {
                    Prerequisites = [new DepFixture("marker", "*")],
                    Files = { ["app.txt"] = "two" },
                },
            ],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("app", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        fixture.WriteConfig("win-x64", new SpecFixture("app", "*"));

        var item = Assert.Single(client.Outdated(fixture.Root));

        Assert.Equal("app", item.Name);
        Assert.Equal("2.0.0", item.LatestInstallable);
    }

    [Fact]
    public void Outdated_UsesTransitiveDependencyRanges()
    {
        using var fixture = new Fixture();
        fixture.AddSei("app", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("lib", "[1.0.0]")],
            Artifacts = [new ArtifactFixture { Files = { ["app.txt"] = "app" } }],
        });
        fixture.AddSei("lib", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["lib.txt"] = "lib" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("app", "*"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        fixture.AddSei("lib", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["lib.txt"] = "lib2" } }],
        });

        var item = Assert.Single(client.Outdated(fixture.Root));

        Assert.Equal("lib", item.Name);
        Assert.Equal("1.0.0", item.LatestInRange);
        Assert.Equal("2.0.0", item.Latest);
    }

    [Fact]
    public void Outdated_UsesEveryRangeForTheSameName()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "one" } }],
        });
        fixture.AddSei("parent", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("hello", "[1.0.0,2.0.0)")],
            Artifacts = [new ArtifactFixture { Files = { ["parent.txt"] = "parent" } }],
        });
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("hello", "[1.0.0,3.0.0)"),
            new SpecFixture("parent", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "two" } }],
        });

        var item = Assert.Single(client.Outdated(fixture.Root));

        Assert.Equal("2.0.0", item.Latest);
        Assert.Equal("1.0.0", item.LatestInRange);
    }

    [Fact]
    public void Outdated_UsesArtifactDependencyRanges()
    {
        using var fixture = new Fixture();
        fixture.AddSei("app", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["app.txt"] = "app" },
                    Dependencies = [new DepFixture("lib", "[1.0.0]")],
                },
            ],
        });
        fixture.AddSei("lib", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["lib.txt"] = "lib" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("app", "*"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        fixture.AddSei("lib", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["lib.txt"] = "lib2" } }],
        });

        var item = Assert.Single(client.Outdated(fixture.Root));

        Assert.Equal("lib", item.Name);
        Assert.Equal("1.0.0", item.LatestInRange);
        Assert.Equal("2.0.0", item.Latest);
        Assert.Contains(item.Blockers, blocker =>
            blocker.Relation == "dependency" && blocker.Target == "app");
    }

    [Fact]
    public void Outdated_AllowsPrereleasesWhenTheRangeNamesOne()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0-beta.1",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "beta1" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0-beta,3.0.0)"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0-beta.2",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "beta2" } }],
        });

        var item = Assert.Single(client.Outdated(fixture.Root));

        Assert.Equal("2.0.0-beta.2", item.LatestInRange);
    }

    [Fact]
    public void Add_FetchesIndexMetadataOncePerCommand()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            "schemaVersion = 2\n"
            + $"sources = [\n  {{ id = \"http\", index = \"{server.BaseUrl}index.json\" }}\n]\n"
            + "rid = \"win-x64\"\n"
            + "sei = []\n");

        new SeikoClient().Add(fixture.Root, "hello", fixture.StoreDirectory);

        // The default-range pre-resolve and the final sync share one source
        // cache; the same metadata is not requested twice.
        Assert.Single(server.Requests, request => request.Path == "/index.json");
        Assert.Single(server.Requests, request => request.Path == "/sei/hello.json");
    }

    [Fact]
    public void Outdated_KeepsPrereleasePermissionWhenRangesIntersect()
    {
        using var fixture = new Fixture();
        fixture.AddSei("lib", new VersionFixture
        {
            Version = "1.0.0-alpha",
            Artifacts = [new ArtifactFixture { Files = { ["lib.txt"] = "alpha" } }],
        });
        fixture.AddSei("app", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["app.txt"] = "app" } }],
        });
        // The direct range names a prerelease; intersecting it with a stable
        // range drops the alpha endpoint, so the permission must come from the
        // original constraints.
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("lib", "[1.0.0-alpha,3.0.0)"),
            new SpecFixture("app", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var path = Path.Combine(fixture.SourceDirectory, "sei", "lib.json");
        var sei = System.Text.Json.JsonSerializer.Deserialize<SeiIndex>(
            File.ReadAllBytes(path),
            SeikoJsonContext.Default.SeiIndex)!;
        sei.Versions.Add(new SeiVersionEntry { Version = "2.1.0-beta.1" });
        File.WriteAllBytes(
            path,
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(sei, SeikoJsonContext.Default.SeiIndex));

        var item = Assert.Single(client.Outdated(fixture.Root), entry => entry.Name == "lib");
        Assert.Equal("2.1.0-beta.1", item.LatestInRange);
    }

    [Fact]
    public void AddPreview_FetchesIndexMetadataOncePerCommand()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");

        new SeikoClient().Add(fixture.Root, "hello", fixture.StoreDirectory, dryRun: true);

        Assert.Single(server.Requests, request => request.Path == "/index.json");
        Assert.Single(server.Requests, request => request.Path == "/sei/hello.json");
    }

    [Fact]
    public void UpdateLatest_FetchesIndexMetadataOncePerCommand()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "one" } }],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("hello"));

        new SeikoClient().Update(fixture.Root, "hello", latest: true, storeRoot: fixture.StoreDirectory);

        Assert.Single(server.Requests, request => request.Path == "/index.json");
        Assert.Single(server.Requests, request => request.Path == "/sei/hello.json");
    }

    [Fact]
    public void Sync_RejectsOverlongArchiveEntryNames()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { [new string('a', ArchiveLimits.MaxEntryNameLength + 1)] = "x" },
                },
            ],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("io", error.Code);
        Assert.Contains("too long", error.Message);
    }

    [Fact]
    public void Sync_RejectsDeepArchiveEntryNames()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files =
                    {
                        [string.Join('/', Enumerable.Repeat("a", ArchiveLimits.MaxEntryNameDepth + 1))] = "x",
                    },
                },
            ],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("io", error.Code);
        Assert.Contains("too deep", error.Message);
    }

    [Fact]
    public void Config_RejectsDeclarationWithoutNameBeforeDeploying()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        var deployed = Path.Combine(fixture.Root, "hello.txt");
        Assert.True(File.Exists(deployed));

        fixture.WriteConfig("win-x64");
        var configPath = Path.Combine(fixture.Root, "seiko.conf");
        File.WriteAllText(
            configPath,
            File.ReadAllText(configPath).Replace(
                "sei = []",
                "sei = [\n  { version = \"[1.0.0]\" }\n]"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("config", error.Code);
        Assert.True(File.Exists(deployed));
    }

    [Fact]
    public void Search_FallsThroughWhenTheFirstSourceCannotProvideThePackage()
    {
        using var first = new Fixture();
        first.AddSei("foo", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["foo.txt"] = "first" } }],
        });
        first.WriteConfig("win-x64");
        File.Delete(Path.Combine(first.SourceDirectory, "sei", "foo.json"));
        // Without the catalog the name list is walked, which is what this
        // test needs: a listed name whose file the source cannot serve.
        File.Delete(Path.Combine(first.SourceDirectory, "catalog.json"));

        using var second = new Fixture();
        second.AddSei("foo", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["foo.txt"] = "second" } }],
        });
        second.WriteConfig("win-x64");
        var firstIndex = Path.Combine(first.SourceDirectory, "index.json").Replace('\\', '/');
        var secondIndex = Path.Combine(second.SourceDirectory, "index.json").Replace('\\', '/');
        File.WriteAllText(
            Path.Combine(first.Root, "seiko.conf"),
            $$"""
            schemaVersion = {{SeikoConfig.CurrentSchemaVersion}}
            sources = [
              { id = "first", index = "{{firstIndex}}" },
              { id = "second", index = "{{secondIndex}}" }
            ]
            rid = "win-x64"
            sei = []
            """);

        var result = Assert.Single(new SeikoClient().Search(first.Root, "foo"));

        Assert.Equal("foo", result.Name);
        Assert.Equal("second", result.Source);
    }

    [Fact]
    public void Search_DoesNotReadDuplicateNamesFromLaterSources()
    {
        using var first = new Fixture();
        first.AddSei("foo", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["foo.txt"] = "first" } }],
        });
        first.WriteConfig("win-x64");

        using var second = new Fixture();
        second.AddSei("foo", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["foo.txt"] = "second" } }],
        });
        second.WriteConfig("win-x64");
        File.WriteAllText(Path.Combine(second.SourceDirectory, "sei", "foo.json"), "{broken");
        var firstIndex = Path.Combine(first.SourceDirectory, "index.json").Replace('\\', '/');
        var secondIndex = Path.Combine(second.SourceDirectory, "index.json").Replace('\\', '/');
        File.WriteAllText(
            Path.Combine(first.Root, "seiko.conf"),
            $$"""
            schemaVersion = {{SeikoConfig.CurrentSchemaVersion}}
            sources = [
              { id = "first", index = "{{firstIndex}}" },
              { id = "second", index = "{{secondIndex}}" }
            ]
            rid = "win-x64"
            sei = []
            """);

        var result = Assert.Single(new SeikoClient().Search(first.Root, "foo"));

        Assert.Equal("first", result.Source);
    }

    [Fact]
    public void Outdated_ReportsLatestInstallable()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "hello",
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "one" } }],
            },
            new VersionFixture
            {
                Version = "2.0.0",
                Artifacts =
                [
                    new ArtifactFixture
                    {
                        Rid = "linux-x64",
                        Files = { ["hello.txt"] = "two" },
                    },
                ],
            });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "*"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory, latestInstallable: true);

        var item = Assert.Single(client.Outdated(fixture.Root));

        Assert.Equal("1.0.0", item.Current);
        Assert.Equal("2.0.0", item.Latest);
        Assert.Equal("1.0.0", item.LatestInstallable);
    }

    [Fact]
    public void Cache_StatusVerifyAndClean()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        var client = new SeikoClient();

        Assert.Equal(1, client.CacheStatus(fixture.StoreDirectory).Artifacts);
        Assert.Empty(client.CacheVerify(fixture.StoreDirectory));

        var stale = Path.Combine(fixture.StoreDirectory, new string('a', 64));
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "junk"), "junk");
        Assert.Single(client.CacheVerify(fixture.StoreDirectory));

        var result = client.CacheClean(fixture.Root, fixture.StoreDirectory);

        Assert.Equal(1, result.Removed);
        Assert.False(Directory.Exists(stale));
        Assert.Equal(1, client.CacheStatus(fixture.StoreDirectory).Artifacts);
    }

    [Fact]
    public void Cache_ReportsAndRemovesInterruptedExtractions()
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
        var digest = Assert.Single(client.ReadLock(fixture.Root)!.Sei).Artifact.Sha256;
        var staging = Path.Combine(
            fixture.StoreDirectory,
            digest + ".tmp-" + new string('a', 32));
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "payload.bin"), "leftover");

        var status = client.CacheStatus(fixture.StoreDirectory);
        Assert.Equal(1, status.StagingItems);
        Assert.Equal(8, status.StagingBytes);
        Assert.Contains(
            client.CacheVerify(fixture.StoreDirectory),
            finding => finding.Code == "cache-staging");

        var clean = client.CacheClean(fixture.Root, fixture.StoreDirectory);

        Assert.Equal(1, clean.Removed);
        Assert.False(Directory.Exists(staging));
        Assert.Equal(0, client.CacheStatus(fixture.StoreDirectory).StagingItems);
    }

    [Fact]
    public void CacheClean_SkipsAnActivelyExtractedStagingDirectory()
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
        var digest = Assert.Single(client.ReadLock(fixture.Root)!.Sei).Artifact.Sha256;
        var staging = Path.Combine(
            fixture.StoreDirectory,
            digest + ".tmp-" + new string('b', 32));
        Directory.CreateDirectory(staging);
        using var held = ProcessLock.Acquire(
            Path.Combine(fixture.StoreDirectory, ".locks", digest + ".lock"),
            "test extraction");

        var clean = client.CacheClean(fixture.Root, fixture.StoreDirectory);

        Assert.Equal(0, clean.Removed);
        Assert.True(Directory.Exists(staging));
    }

    [Fact]
    public void Doctor_ReportsAndRemovesInterruptedExtractions()
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
        var digest = Assert.Single(client.ReadLock(fixture.Root)!.Sei).Artifact.Sha256;
        var staging = Path.Combine(
            fixture.StoreDirectory,
            digest + ".tmp-" + new string('c', 32));
        Directory.CreateDirectory(staging);

        var reported = new Doctor().Run(fixture.Root, fixture.StoreDirectory);
        Assert.Contains(reported.Findings, finding => finding.Code == "staging");

        var cleaned = new Doctor().Run(fixture.Root, fixture.StoreDirectory, clean: true);

        Assert.False(Directory.Exists(staging));
        Assert.Equal(1, cleaned.CleanCount);
    }

    [Fact]
    public void CacheClean_ProtectsAllRegisteredRoots()
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
        var stale = Path.Combine(sharedStore, new string('b', 64));
        Directory.CreateDirectory(stale);

        var result = client.CacheClean(first.Root, sharedStore);

        Assert.Equal(1, result.Removed);
        Assert.False(Directory.Exists(stale));
        Assert.Equal(2, client.CacheStatus(sharedStore).Artifacts);
    }

    [Fact]
    public void CacheClean_ProtectsArtifactsNeededByRollback()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "hello",
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v1" } }],
            },
            new VersionFixture
            {
                Version = "2.0.0",
                Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v2" } }],
            });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var clean = client.CacheClean(fixture.Root, fixture.StoreDirectory);

        Assert.Equal(0, clean.Removed);
        var rollback = client.Rollback(fixture.Root, fixture.StoreDirectory);
        Assert.Equal(1, rollback.Generation);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Update_WithoutLatestKeepsRangeAndUpgrades()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "one" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0,3.0.0)"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "two" } }],
        });

        new SeikoClient().Update(fixture.Root, "hello", storeRoot: fixture.StoreDirectory);

        Assert.Equal("two", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
        Assert.Contains("[1.0.0,3.0.0)", File.ReadAllText(Path.Combine(fixture.Root, "seiko.conf")));
    }
}
