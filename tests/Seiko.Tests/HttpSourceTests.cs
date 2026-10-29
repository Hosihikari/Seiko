using Xunit;

namespace Seiko.Tests;

public sealed class HttpSourceTests
{
    [Fact]
    public void Sync_DownloadsFromHttpSource()
    {
        using var fixture = new Fixture();
        fixture.AddPackage("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("hello", "[1.0.0]"));

        var result = new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal(1, result.SeiInstalled);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Search_WorksWithHttpSource()
    {
        using var fixture = new Fixture();
        fixture.AddPackage("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");

        var results = new SeikoClient().Search(fixture.Root, "hel");

        var result = Assert.Single(results);
        Assert.Equal("hello", result.Name);
        Assert.Equal("http", result.Source);
    }
}
