using Xunit;

namespace Seiko.Tests;

public sealed class LockReplayRegressionTests
{
    [Fact]
    public void Frozen_ReplaysSameNameWithMultipleRangesInAnyOrder()
    {
        using var fixture = new Fixture();
        fixture.AddSei("bbb", new VersionFixture
        {
            Version = "2.1.0",
            Artifacts = [new ArtifactFixture { Files = { ["b.txt"] = "b" } }],
        });
        fixture.AddSei("aaa", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("bbb", "[2.0.0,)"), new DepFixture("bbb", "(,3.0.0]")],
            Artifacts = [new ArtifactFixture { Files = { ["a.txt"] = "a" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("aaa", "[1.0.0]"), new SpecFixture("bbb", "*"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var result = client.Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen);

        Assert.Equal(2, result.SeiInstalled);
    }
}
