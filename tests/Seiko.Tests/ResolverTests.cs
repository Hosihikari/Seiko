using Xunit;

namespace Seiko.Tests;

public sealed class ResolverTests
{
    [Fact]
    public void Resolve_ReconsidersAVersionWhenALaterConstraintNarrowsIt()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "b",
            Version("b", "1.5.0"),
            Version("b", "2.5.0"));
        fixture.AddSei("a", Version("a", "1.0.0", new DepFixture("b", "[0.0.0,3.0.0)")));
        fixture.AddSei("c", Version("c", "1.0.0", new DepFixture("b", "[0.0.0,2.0.0)")));
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("a", "[1.0.0]"),
            new SpecFixture("c", "[1.0.0]"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        var lockFile = new SeikoClient().ReadLock(fixture.Root)!;
        Assert.Equal("1.5.0", lockFile.Sei.Single(sei => sei.Name == "b").Version);
    }

    [Fact]
    public void Resolve_PrunesDependenciesOfReselectedVersions()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "a",
            Version("a", "1.0.0"),
            Version("a", "2.0.0", new DepFixture("x", "[1.0.0]")));
        fixture.AddSei("x", Version("x", "1.0.0"));
        fixture.AddSei("d", Version("d", "1.0.0", new DepFixture("a", "[1.0.0,2.0.0)")));
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("a"),
            new SpecFixture("d", "[1.0.0]"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        var lockFile = new SeikoClient().ReadLock(fixture.Root)!;
        Assert.Equal("1.0.0", lockFile.Sei.Single(sei => sei.Name == "a").Version);
        Assert.DoesNotContain(lockFile.Sei, sei => sei.Name == "x");
        Assert.Contains(lockFile.Sei, sei => sei.Name == "d");
    }

    [Fact]
    public void Resolve_BacktracksToAnEarlierPackageVersion()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "a",
            Version("a", "1.0.0"),
            Version("a", "2.0.0", new DepFixture("b", "[2.0.0,3.0.0)")));
        fixture.AddSei(
            "b",
            Version("b", "1.5.0"),
            Version("b", "2.5.0"));
        fixture.AddSei("c", Version("c", "1.0.0", new DepFixture("b", "[0.0.0,2.0.0)")));
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("a"),
            new SpecFixture("c", "[1.0.0]"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        var lockFile = new SeikoClient().ReadLock(fixture.Root)!;
        Assert.Equal("1.0.0", lockFile.Sei.Single(sei => sei.Name == "a").Version);
        Assert.Equal("1.5.0", lockFile.Sei.Single(sei => sei.Name == "b").Version);
        Assert.Equal("1.0.0", lockFile.Sei.Single(sei => sei.Name == "c").Version);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_AllowsPrereleaseRequestedByALaterDependency(bool directFirst)
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "a",
            Version("a", "1.0.0"),
            Version("a", "2.0.0-beta"));
        fixture.AddSei("b", Version("b", "1.0.0", new DepFixture("a", "[2.0.0-beta]")));
        SpecFixture[] specs = directFirst
            ? [new SpecFixture("a"), new SpecFixture("b", "[1.0.0]")]
            : [new SpecFixture("b", "[1.0.0]"), new SpecFixture("a")];
        fixture.WriteConfig("win-x64", specs);

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        var lockFile = new SeikoClient().ReadLock(fixture.Root)!;
        Assert.Equal("2.0.0-beta", lockFile.Sei.Single(sei => sei.Name == "a").Version);
        Assert.Equal("1.0.0", lockFile.Sei.Single(sei => sei.Name == "b").Version);
    }

    [Fact]
    public void Resolve_LatestInstallableSkipsVersionsWithoutArtifacts()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "a",
            Version("a", "1.0.0"),
            new VersionFixture
            {
                Version = "2.0.0",
                Artifacts =
                [
                    new ArtifactFixture
                    {
                        Selectors = new Dictionary<string, string> { ["rid"] = "linux-x64" },
                        Files = { ["a.txt"] = "two" },
                    },
                ],
            });
        fixture.WriteConfig("win-x64", new SpecFixture("a", "*"));
        var client = new SeikoClient();

        var error = Assert.Throws<SeikoException>(
            () => client.Sync(fixture.Root, fixture.StoreDirectory));
        Assert.Equal("resolve", error.Code);

        client.Sync(fixture.Root, fixture.StoreDirectory, latestInstallable: true);

        Assert.Equal("1.0.0", client.ReadLock(fixture.Root)!.Sei.Single().Version);
    }

    [Fact]
    public void Resolve_LatestInstallableRequiresCarrierInTheGraph()
    {
        using var withoutCarrier = new Fixture();
        withoutCarrier.AddSei(
            "a",
            Version("a", "1.0.0"),
            new VersionFixture
            {
                Version = "2.0.0",
                Artifacts =
                [
                    new ArtifactFixture
                    {
                        Selectors = new Dictionary<string, string> { ["carrier"] = "marker" },
                        Files = { ["a.txt"] = "two" },
                    },
                ],
            });
        withoutCarrier.AddSei("marker", Version("marker", "1.0.0"));
        withoutCarrier.WriteConfig("win-x64", new SpecFixture("a", "*"));
        var client = new SeikoClient();
        client.Sync(withoutCarrier.Root, withoutCarrier.StoreDirectory, latestInstallable: true);
        Assert.Equal("1.0.0", client.ReadLock(withoutCarrier.Root)!.Sei.Single().Version);

        using var withCarrier = new Fixture();
        withCarrier.AddSei(
            "a",
            Version("a", "1.0.0"),
            new VersionFixture
            {
                Version = "2.0.0",
                Artifacts =
                [
                    new ArtifactFixture
                    {
                        Selectors = new Dictionary<string, string> { ["carrier"] = "marker" },
                        Files = { ["a.txt"] = "two" },
                    },
                ],
            });
        withCarrier.AddSei("marker", Version("marker", "1.0.0"));
        withCarrier.WriteConfig(
            "win-x64",
            new SpecFixture("a", "*"),
            new SpecFixture("marker", "*"));
        client.Sync(withCarrier.Root, withCarrier.StoreDirectory, latestInstallable: true);
        Assert.Equal("2.0.0", client.ReadLock(withCarrier.Root)!.Sei.Single(sei => sei.Name == "a").Version);
    }

    [Fact]
    public void Resolve_LatestInstallableIsRejectedWithFrozen()
    {
        using var fixture = new Fixture();
        fixture.AddSei("a", Version("a", "1.0.0"));
        fixture.WriteConfig("win-x64", new SpecFixture("a", "*"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var error = Assert.Throws<SeikoException>(
            () => client.Sync(
                fixture.Root,
                fixture.StoreDirectory,
                mode: SyncMode.Frozen,
                latestInstallable: true));

        Assert.Equal("usage", error.Code);
    }

    [Fact]
    public void Resolve_DoesNotTreatBuildMetadataAsPrereleaseAuthorization()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "a",
            Version("a", "1.0.0"),
            Version("a", "2.0.0-beta"));
        fixture.WriteConfig("win-x64", new SpecFixture("a", "[1.0.0,3.0.0+abc-def]"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        var lockFile = new SeikoClient().ReadLock(fixture.Root)!;
        Assert.Equal("1.0.0", lockFile.Sei.Single(sei => sei.Name == "a").Version);
    }

    [Fact]
    public void Resolve_RejectsPrereleaseAuthorizedOnlyByAnAbandonedBranch()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "a",
            Version("a", "1.0.0"),
            Version("a", "2.0.0-beta"));
        fixture.AddSei(
            "b",
            Version("b", "1.0.0", new DepFixture("a", "[1.5.0,3.0.0)")),
            Version(
                "b",
                "2.0.0",
                new DepFixture("a", "[2.0.0-beta]"),
                new DepFixture("c", "[2.0.0]")));
        fixture.AddSei("c", Version("c", "1.0.0"));
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("b"),
            new SpecFixture("a"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("resolve", error.Code);
        Assert.Contains("prerelease", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "a.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "seiko.lock.json")));
    }

    [Fact]
    public void Resolve_PrefersStableWhenAPrereleaseHintComesFromAnAbandonedBranch()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "a",
            Version("a", "1.0.0"),
            Version("a", "2.0.0-beta"));
        fixture.AddSei(
            "b",
            Version("b", "1.0.0", new DepFixture("a", "[1.0.0,2.0.0)")),
            Version(
                "b",
                "2.0.0",
                new DepFixture("a", "[2.0.0-beta]"),
                new DepFixture("c", "[2.0.0]")));
        fixture.AddSei("c", Version("c", "1.0.0"));
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("b"),
            new SpecFixture("a"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        var lockFile = new SeikoClient().ReadLock(fixture.Root)!;
        Assert.Equal("1.0.0", lockFile.Sei.Single(sei => sei.Name == "a").Version);
        Assert.Equal("1.0.0", lockFile.Sei.Single(sei => sei.Name == "b").Version);
    }

    [Fact]
    public void Resolve_KeepsSearchingAfterManyDistinctPrereleaseHints()
    {
        using var fixture = new Fixture();
        var versions = new List<VersionFixture>();
        for (var i = 1; i <= 101; i++)
        {
            versions.Add(Version(
                "b",
                $"2.0.0-alpha.{i}",
                new DepFixture("a", $"[3.0.0-beta.{i}]")));
        }

        versions.Add(Version("b", "1.0.0"));
        fixture.AddSei("b", [.. versions]);
        fixture.AddSei("a", Version("a", "1.0.0"));
        fixture.WriteConfig("win-x64", new SpecFixture("b", "[1.0.0-alpha,3.0.0)"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        var lockFile = new SeikoClient().ReadLock(fixture.Root)!;
        Assert.Equal("1.0.0", lockFile.Sei.Single(sei => sei.Name == "b").Version);
        Assert.DoesNotContain(lockFile.Sei, sei => sei.Name == "a");
    }

    [Fact]
    public void Resolve_ReportsTheConstraintChainOnConflict()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "b",
            Version("b", "1.5.0"),
            Version("b", "2.5.0"));
        fixture.AddSei("a", Version("a", "1.0.0", new DepFixture("b", "[0.0.0,2.0.0)")));
        fixture.AddSei("c", Version("c", "1.0.0", new DepFixture("b", "[2.0.0,3.0.0)")));
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("a", "[1.0.0]"),
            new SpecFixture("c", "[1.0.0]"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("resolve", error.Code);
        Assert.Contains("required by a", error.Message);
        Assert.Contains("required by c", error.Message);
    }

    private static VersionFixture Version(string name, string version, params DepFixture[] dependencies)
        => new()
        {
            Version = version,
            Dependencies = [.. dependencies],
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { [name + ".txt"] = version },
                },
            ],
        };
}
