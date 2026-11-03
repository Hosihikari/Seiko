using Xunit;

namespace Seiko.Tests;

public sealed class ResolverRegressionTests
{
    [Fact]
    public void Resolve_ReportsAmbiguityWhenATiedArtifactNeedsNewDependencies()
    {
        using var fixture = new Fixture();
        fixture.AddSei("marker", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["m.txt"] = "m" } }],
        });
        fixture.AddSei("aaa", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture { Files = { ["a.txt"] = "plain" } },
                new ArtifactFixture
                {
                    Dependencies = [new DepFixture("marker", "*")],
                    Files = { ["a.txt"] = "with" },
                },
            ],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("aaa", "[1.0.0]"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("resolve", error.Code);
        Assert.Contains("ambiguous artifact", error.Message);
    }
}
