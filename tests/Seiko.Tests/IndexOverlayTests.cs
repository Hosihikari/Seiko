using System.Text.Json;
using Xunit;

namespace Seiko.Tests;

// The source overlay is merged into the standard retirement fields on read, so
// a plain 0.3-era consumer sees the source's yank and advisory without any new
// code, and the index stays a single shape for everyone.
public sealed class IndexOverlayTests
{
    private static SeiIndex Read(Fixture fixture)
        => JsonSerializer.Deserialize(
            File.ReadAllBytes(Path.Combine(fixture.SourceDirectory, "sei", "hello.json")),
            SeikoJsonContext.Default.SeiIndex)!;

    private static void Write(Fixture fixture, SeiIndex sei)
        => File.WriteAllBytes(
            Path.Combine(fixture.SourceDirectory, "sei", "hello.json"),
            JsonSerializer.SerializeToUtf8Bytes(sei, SeikoJsonContext.Default.SeiIndex));

    private static Fixture FixtureWithTwoVersions()
    {
        var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.WriteConfig("win-x64");
        return fixture;
    }

    [Fact]
    public void SourceYankHidesTheVersionFromFreshResolution()
    {
        using var fixture = FixtureWithTwoVersions();
        var sei = Read(fixture);
        sei.Versions.Single(entry => entry.Version == "2.0.0").SourceYanked = true;
        Write(fixture, sei);
        fixture.RefreshCatalog();
        fixture.WriteConfig("win-x64", new SpecFixture("hello"));

        var result = new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        var lockFile = LockFile.Read(Path.Combine(fixture.Root, "seiko.lock.json"))!;
        Assert.Equal("1.0.0", Assert.Single(lockFile.Sei).Version);
        Assert.Equal(1, result.SeiInstalled);
    }

    [Fact]
    public void MaintainerYankStillWins()
    {
        using var fixture = FixtureWithTwoVersions();
        var sei = Read(fixture);
        var entry = sei.Versions.Single(item => item.Version == "2.0.0");
        entry.Yanked = true;
        entry.SourceYanked = false;
        Write(fixture, sei);
        fixture.RefreshCatalog();
        fixture.WriteConfig("win-x64", new SpecFixture("hello"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        var lockFile = LockFile.Read(Path.Combine(fixture.Root, "seiko.lock.json"))!;
        Assert.Equal("1.0.0", Assert.Single(lockFile.Sei).Version);
    }

    [Fact]
    public void SourceAdvisoryIsMergedAndTheMoreSevereOneWins()
    {
        using var fixture = FixtureWithTwoVersions();
        var sei = Read(fixture);
        var entry = sei.Versions.Single(item => item.Version == "2.0.0");
        entry.Advisory = new AdvisoryInfo { Severity = "moderate", Url = "https://example.invalid/m" };
        entry.SourceAdvisories =
        [
            new AdvisoryInfo { Severity = "critical", Url = "https://example.invalid/c" },
        ];
        Write(fixture, sei);
        fixture.RefreshCatalog();
        fixture.WriteConfig("win-x64", new SpecFixture("hello"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        var lockFile = LockFile.Read(Path.Combine(fixture.Root, "seiko.lock.json"))!;
        Assert.Equal("1.0.0", Assert.Single(lockFile.Sei).Version);
    }

    [Fact]
    public void LowerSourceAdvisoryKeepsTheMaintainerSeverity()
    {
        using var fixture = FixtureWithTwoVersions();
        var sei = Read(fixture);
        var entry = sei.Versions.Single(item => item.Version == "2.0.0");
        entry.Advisory = new AdvisoryInfo { Severity = "critical", Url = "https://example.invalid/c" };
        entry.SourceAdvisories =
        [
            new AdvisoryInfo { Severity = "low", Url = "https://example.invalid/l" },
        ];
        Write(fixture, sei);
        fixture.RefreshCatalog();
        fixture.WriteConfig("win-x64", new SpecFixture("hello"));

        // Critical blocks a fresh resolution; it does not fail the sync. The
        // maintainer's critical severity survives a lower source advisory.
        var result = new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        Assert.Equal("1.0.0", Assert.Single(result.Lock.Sei).Version);
        Assert.Contains(result.Warnings, warning => warning.Contains("critical", StringComparison.Ordinal));
    }

    [Fact]
    public void SourceUnlistedHidesThePackageFromSearchButKeepsOwnership()
    {
        using var fixture = FixtureWithTwoVersions();
        var sei = Read(fixture);
        sei.Unlisted = false;
        sei.SourceUnlisted = true;
        Write(fixture, sei);
        fixture.RefreshCatalog();

        var results = new SeikoClient().Search(fixture.Root, "hello");

        Assert.Empty(results);
    }

    [Fact]
    public void SourceDeprecatedOverridesTheMaintainerText()
    {
        using var fixture = FixtureWithTwoVersions();
        var sei = Read(fixture);
        sei.Deprecated = "maintainer text";
        sei.SourceDeprecated = "source text";
        Write(fixture, sei);
        fixture.RefreshCatalog();

        var info = new SeikoClient().Info(fixture.Root, "hello");

        Assert.Equal("source text", info.Deprecated);
    }

    [Fact]
    public void InfoMarksSourceOriginAndUpstreamHost()
    {
        using var fixture = FixtureWithTwoVersions();
        var sei = Read(fixture);
        sei.Upstream = new UpstreamInfo
        {
            Url = "https://maintainer.invalid/sei-index.json",
            Sha256 = new string('a', 64),
        };
        sei.Versions.Single(item => item.Version == "2.0.0").SourceYanked = true;
        Write(fixture, sei);
        fixture.RefreshCatalog();

        var info = new SeikoClient().Info(fixture.Root, "hello");

        Assert.Equal("maintainer.invalid", info.UpstreamHost);
        Assert.True(info.MaintainedHere);
        Assert.True(info.Versions.Single(item => item.Version == "2.0.0").SourceRetired);
        Assert.False(info.Versions.Single(item => item.Version == "1.0.0").SourceRetired);
    }

    [Fact]
    public void ProjectMetadataReachesSearchAndInfo()
    {
        using var fixture = FixtureWithTwoVersions();
        var sei = Read(fixture);
        sei.Project = new ProjectMetadata
        {
            Description = "hello world",
            Authors = ["someone"],
            License = "MIT",
            Tags = ["greeting"],
        };
        Write(fixture, sei);
        fixture.RefreshCatalog();

        var result = Assert.Single(new SeikoClient().Search(fixture.Root, "world"));
        Assert.Equal("hello world", result.Description);
        Assert.Equal("MIT", result.License);
        Assert.Equal(["greeting"], result.Tags);

        var info = new SeikoClient().Info(fixture.Root, "hello");
        Assert.Equal("someone", Assert.Single(info.Project!.Authors));
    }
}
