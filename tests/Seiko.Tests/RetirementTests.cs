using Xunit;

namespace Seiko.Tests;

public sealed class RetirementTests
{
    [Fact]
    public void Sync_SkipsCriticalVersionAndWarns()
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
                Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "two" } }],
                Advisory = new AdvisoryInfo
                {
                    Severity = "critical",
                    Url = "https://example.com/advisories/1",
                },
            });
        fixture.WriteConfig("win-x64", new SpecFixture("hello"));

        var result = new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("1.0.0", Assert.Single(result.Lock.Sei).Version);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("skipped critical version(s) of hello: 2.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public void Sync_TrimsCriticalSeverity()
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
                Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "two" } }],
                Advisory = new AdvisoryInfo { Severity = " CRITICAL " },
            });
        fixture.WriteConfig("win-x64", new SpecFixture("hello"));

        var result = new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("1.0.0", Assert.Single(result.Lock.Sei).Version);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("skipped critical version(s) of hello: 2.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public void Sync_SelectsCriticalVersionWithAllowCritical()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "two" } }],
            Advisory = new AdvisoryInfo
            {
                Severity = "critical",
                Url = "https://example.com/advisories/1",
            },
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));

        var result = new SeikoClient().Sync(
            fixture.Root,
            fixture.StoreDirectory,
            allowCritical: true);

        Assert.Equal("2.0.0", Assert.Single(result.Lock.Sei).Version);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("hello 2.0.0 is critical", StringComparison.Ordinal));
    }

    [Fact]
    public void Sync_SkipsYankedVersionSilently()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "hello",
            new VersionFixture
            {
                Version = "1.0.0",
                Yanked = true,
                Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "one" } }],
            },
            new VersionFixture
            {
                Version = "2.0.0",
                Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "two" } }],
            });
        fixture.WriteConfig("win-x64", new SpecFixture("hello"));

        var result = new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("2.0.0", Assert.Single(result.Lock.Sei).Version);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Frozen_ReplaysYankedVersionAndWarns()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "one" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        fixture.MarkVersionYanked("hello", "1.0.0");

        var result = client.Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen);

        Assert.Equal("1.0.0", Assert.Single(result.Lock.Sei).Version);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("hello 1.0.0 is yanked", StringComparison.Ordinal));
    }

    [Fact]
    public void Sync_WarnsForDeprecatedVersion()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Deprecated = "use hello2 instead",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "one" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));

        var result = new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Contains(result.Warnings, warning =>
            warning.Contains("deprecated: use hello2 instead", StringComparison.Ordinal));
    }

    [Fact]
    public void Sync_InheritsDeprecatedPackageMessage()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "one" } }],
        });
        fixture.MarkPackage("hello", unlisted: false, deprecated: "the whole package is retired");
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));

        var result = new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Contains(result.Warnings, warning =>
            warning.Contains("deprecated: the whole package is retired", StringComparison.Ordinal));
    }

    [Fact]
    public void Update_LatestSelectsCriticalVersionOnlyWhenAllowed()
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
                Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "two" } }],
                Advisory = new AdvisoryInfo { Severity = "critical" },
            });
        fixture.WriteConfig("win-x64", new SpecFixture("hello"));
        var client = new SeikoClient();

        var skipped = client.Update(fixture.Root, latest: true, storeRoot: fixture.StoreDirectory);
        Assert.Equal("1.0.0", Assert.Single(skipped.Lock.Sei).Version);

        var allowed = client.Update(
            fixture.Root,
            latest: true,
            storeRoot: fixture.StoreDirectory,
            allowCritical: true);
        Assert.Equal("2.0.0", Assert.Single(allowed.Lock.Sei).Version);
        Assert.Contains(allowed.Warnings, warning =>
            warning.Contains("hello 2.0.0 is critical", StringComparison.Ordinal));
    }

    [Fact]
    public void Frozen_ReplaysCriticalVersionAndWarns()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "two" } }],
            Advisory = new AdvisoryInfo { Severity = "critical" },
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory, allowCritical: true);

        var result = client.Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen);

        Assert.Equal("2.0.0", Assert.Single(result.Lock.Sei).Version);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("hello 2.0.0 is critical", StringComparison.Ordinal));
    }

    [Fact]
    public void Search_DoesNotFallThroughWhenTheFirstSourceIsCriticalOnly()
    {
        using var first = new Fixture();
        first.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "first" } }],
            Advisory = new AdvisoryInfo { Severity = "critical" },
        });
        first.WriteConfig("win-x64");

        using var second = new Fixture();
        second.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "second" } }],
        });
        second.WriteConfig("win-x64");
        var firstIndex = Path.Combine(first.SourceDirectory, "index.json").Replace('\\', '/');
        var secondIndex = Path.Combine(second.SourceDirectory, "index.json").Replace('\\', '/');
        File.WriteAllText(
            Path.Combine(first.Root, "seiko.conf"),
            $$"""
            sources = [
              { id = "first", index = "{{firstIndex}}" },
              { id = "second", index = "{{secondIndex}}" }
            ]
            rid = "win-x64"
            sei = []
            """);

        Assert.Empty(new SeikoClient().Search(first.Root, "hello"));
    }

    [Fact]
    public void Search_HidesUnlistedPackageButInfoShowsIt()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "one" } }],
        });
        fixture.MarkPackage("hello", unlisted: true, deprecated: "use hello2 instead");
        fixture.WriteConfig("win-x64");

        var client = new SeikoClient();
        Assert.Empty(client.Search(fixture.Root, "hello"));

        var info = client.Info(fixture.Root, "hello");
        Assert.True(info.Unlisted);
        Assert.Equal("use hello2 instead", info.Deprecated);
    }

    [Fact]
    public void Sync_EmptyYankedSelectionFailsWithoutYankedWarning()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Yanked = true,
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "one" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("resolve", error.Code);
        Assert.DoesNotContain("yanked", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
