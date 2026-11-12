using Xunit;

namespace Seiko.Tests;

public sealed class HttpSourceTests
{
    [Fact]
    public void HttpSource_SendsAClientUserAgent()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        var agent = Assert.Single(server.UserAgents);
        Assert.StartsWith("sk/", agent, StringComparison.Ordinal);
        Assert.Contains("hoshi.srcz.one/seiko", agent, StringComparison.Ordinal);
    }

    [Fact]
    public void HttpSource_SendsTheConfiguredUserAgentInsteadOfTheDefault()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");

        using var agent = Fixture.UseUserAgent("my-gateway/1.0");
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("my-gateway/1.0", Assert.Single(server.UserAgents));

        // A value that is only whitespace counts as unset.
        using var empty = Fixture.UseUserAgent("   ");
        var again = new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        Assert.NotNull(again);
        Assert.StartsWith("sk/", server.UserAgents[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Sync_DownloadsFromHttpSource()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
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
        fixture.AddSei("hello", new VersionFixture
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
