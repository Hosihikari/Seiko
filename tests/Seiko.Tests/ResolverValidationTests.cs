using Xunit;

namespace Seiko.Tests;

public sealed class ResolverValidationTests
{
    [Fact]
    public void Sync_RejectsSelfDependency()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("hello", "*")],
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("resolve", error.Code);
        Assert.Contains("depends on itself", error.Message);
    }

    [Fact]
    public void Sync_RejectsNullDependencyListFromSourceJson()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello"));
        File.WriteAllText(
            Path.Combine(fixture.SourceDirectory, "sei", "hello.json"),
            """{"schemaVersion":2,"name":"hello","versions":[{"version":"1.0.0","dependencies":null,"artifacts":[]}]}""");

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("resolve", error.Code);
        Assert.Contains("dependency list", error.Message);
    }

    [Fact]
    public void Sync_RejectsNullArtifactListFromSourceJson()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello"));
        File.WriteAllText(
            Path.Combine(fixture.SourceDirectory, "sei", "hello.json"),
            """{"schemaVersion":2,"name":"hello","versions":[{"version":"1.0.0","dependencies":[],"artifacts":null}]}""");

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("resolve", error.Code);
        Assert.Contains("artifact list", error.Message);
    }

    [Fact]
    public void Sync_RejectsEmptyRidFromSourceJson()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello"));
        File.WriteAllText(
            Path.Combine(fixture.SourceDirectory, "sei", "hello.json"),
            """{"schemaVersion":2,"name":"hello","versions":[{"version":"1.0.0","dependencies":[],"artifacts":[{"rid":"","layout":"root","source":{"url":"artifacts/x.zip","sha256":"0000000000000000000000000000000000000000000000000000000000000000","size":1}}]}]}""");

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("resolve", error.Code);
        Assert.Contains("empty rid", error.Message);
    }

    [Fact]
    public void Sync_RejectsNullRelationListFromSourceJson()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello"));
        File.WriteAllText(
            Path.Combine(fixture.SourceDirectory, "sei", "hello.json"),
            """{"schemaVersion":2,"name":"hello","versions":[{"version":"1.0.0","dependencies":[],"prerequisites":null,"artifacts":[]}]}""");

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("resolve", error.Code);
        Assert.Contains("relation list", error.Message);
    }

    [Fact]
    public void Resolve_RejectsEmptyVersionList()
    {
        var source = new SingleSource(new SeiIndex { Name = "hello", Versions = [] });
        var resolver = new Resolver([source], null, "win-x64", false);

        var error = Assert.Throws<SeikoException>(
            () => resolver.Resolve([new SeiSpec { Name = "hello" }]));

        Assert.Equal("resolve", error.Code);
        Assert.Contains("has no versions", error.Message);
    }

    [Fact]
    public void Resolve_RejectsNullVersionEntry()
    {
        var source = new SingleSource(new SeiIndex { Name = "hello", Versions = [null!] });
        var resolver = new Resolver([source], null, "win-x64", false);

        var error = Assert.Throws<SeikoException>(
            () => resolver.Resolve([new SeiSpec { Name = "hello" }]));

        Assert.Equal("resolve", error.Code);
        Assert.Contains("invalid version entry", error.Message);
    }

    [Fact]
    public void Sync_RejectsDuplicateVersions()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "hello",
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "first" } }],
            },
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "second" } }],
            });
        fixture.WriteConfig("win-x64", new SpecFixture("hello"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("resolve", error.Code);
        Assert.Contains("duplicate version", error.Message);
    }

    private sealed class SingleSource : ISeiSource
    {
        private readonly SeiIndex? _index;

        public SingleSource(SeiIndex? index)
        {
            _index = index;
        }

        public string Id => "test";

        public IndexRoot Index => new() { Sei = ["hello"] };

        public SeiIndex? GetSei(string name) => _index;

        public string ResolveArtifact(ArtifactLocation source)
            => throw new NotSupportedException();

        public Task DownloadAsync(
            string name,
            ArtifactLocation source,
            string directory,
            DownloadSession session,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
