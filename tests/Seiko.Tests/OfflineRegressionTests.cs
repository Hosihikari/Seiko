using Xunit;

namespace Seiko.Tests;

public sealed class OfflineRegressionTests
{
    [Fact]
    public void OfflineReplay_IgnoresRelationsOfUnselectedVersions()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("aaa", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("unneeded", "*")],
            Artifacts = [new ArtifactFixture { Files = { ["a1.txt"] = "one" } }],
        });
        fixture.AddSei("aaa", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["a2.txt"] = "two" } }],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("aaa", "[2.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var selPath = Path.Combine(fixture.SourceDirectory, "sei", "aaa.json");
        Assert.True(File.Exists(selPath));

        var result = client.Sync(
            fixture.Root,
            fixture.StoreDirectory,
            mode: SyncMode.Locked,
            offline: true);

        Assert.Equal(1, result.SeiInstalled);
    }
}
