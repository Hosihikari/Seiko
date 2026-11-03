using Xunit;

namespace Seiko.Tests;

public sealed class SearchInfoTests
{
    [Fact]
    public void Search_FindsSei()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.AddSei("other", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"), new SpecFixture("other", "[1.0.0]"));

        var results = new SeikoClient().Search(fixture.Root, "hel");

        var result = Assert.Single(results);
        Assert.Equal("hello", result.Name);
        Assert.Equal("1.0.0", result.Version);
        Assert.Equal("local", result.Source);
    }

    [Fact]
    public void Info_ListsVersions()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "hello",
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] },
            new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture(), new ArtifactFixture()] });
        fixture.WriteConfig("win-x64");

        var info = new SeikoClient().Info(fixture.Root, "hello");

        Assert.Equal("hello", info.Name);
        Assert.Equal("local", info.Source);
        Assert.Equal(2, info.Versions.Count);
        Assert.Equal("2.0.0", info.Versions[0].Version);
        Assert.Equal(2, info.Versions[0].ArtifactCount);
    }

    [Fact]
    public void Info_ShowsEveryRelationAndArtifactCondition()
    {
        using var fixture = new Fixture();
        fixture.AddSei("helper", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Prerequisites = [new DepFixture("helper", "*")],
            Conflicts = [new DepFixture("other", "[2.0.0]")],
            Artifacts =
            [
                new ArtifactFixture { Rid = "win-x64", OptionalPeers = [new DepFixture("peer", "*")] },
            ],
        });
        fixture.WriteConfig("win-x64");

        var info = new SeikoClient().Info(fixture.Root, "hello");

        var version = Assert.Single(info.Versions);
        Assert.Equal("helper", Assert.Single(version.Prerequisites).Name);
        Assert.Equal("other", Assert.Single(version.Conflicts).Name);
        var artifact = Assert.Single(version.Artifacts);
        Assert.Equal("win-x64", artifact.Rid);
        Assert.Equal("peer", Assert.Single(artifact.OptionalPeers).Name);
    }

    [Fact]
    public void Info_ThrowsWhenMissing()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.WriteConfig("win-x64");

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().Info(fixture.Root, "missing"));
        Assert.Contains("not found", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
