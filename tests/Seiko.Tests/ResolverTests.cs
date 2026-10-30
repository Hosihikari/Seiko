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
