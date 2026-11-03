using Xunit;

namespace Seiko.Tests;

public sealed class AdoptRollbackTests
{
    [Fact]
    public void Rollback_RestoresTheAdoptedContentOfTheTargetGeneration()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "hello",
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v1" } }] },
            new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v2" } }] });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var deployed = Path.Combine(fixture.Root, "hello.txt");
        File.WriteAllText(deployed, "ADOPTED BY USER");
        client.Sync(fixture.Root, fixture.StoreDirectory, adopt: true);
        client.Sync(fixture.Root, fixture.StoreDirectory, force: true);
        Assert.Equal("v1", File.ReadAllText(deployed));

        var result = client.Rollback(fixture.Root, fixture.StoreDirectory, toGeneration: 2);

        Assert.Equal(2, result.Generation);
        Assert.Equal("ADOPTED BY USER", File.ReadAllText(deployed));
    }

    [Fact]
    public void Rollback_DropsAFileAdoptedOnlyInANewerGeneration()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "aaa",
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts = [new ArtifactFixture { Files = { ["a.txt"] = "a" } }],
            },
            new VersionFixture
            {
                Version = "2.0.0",
                Artifacts =
                [
                    new ArtifactFixture { Files = { ["a.txt"] = "a", ["b.txt"] = "b" } },
                ],
            });
        fixture.WriteConfig("win-x64", new SpecFixture("aaa", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        fixture.WriteConfig("win-x64", new SpecFixture("aaa", "[2.0.0]"));
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var b = Path.Combine(fixture.Root, "b.txt");
        File.WriteAllText(b, "adopted");
        client.Sync(fixture.Root, fixture.StoreDirectory, adopt: true);

        client.Rollback(fixture.Root, fixture.StoreDirectory, toGeneration: 1);

        Assert.False(File.Exists(b));
        Assert.Equal("a", File.ReadAllText(Path.Combine(fixture.Root, "a.txt")));
    }

    [Fact]
    public void Sync_AfterARollbackKeepsTheRestoredAdoptedContent()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "v1" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var deployed = Path.Combine(fixture.Root, "hello.txt");
        File.WriteAllText(deployed, "ADOPTED BY USER");
        client.Sync(fixture.Root, fixture.StoreDirectory, adopt: true);
        client.Sync(fixture.Root, fixture.StoreDirectory, force: true);
        client.Rollback(fixture.Root, fixture.StoreDirectory, toGeneration: 2);

        client.Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("ADOPTED BY USER", File.ReadAllText(deployed));
    }
}
