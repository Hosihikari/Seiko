using Xunit;

namespace Seiko.Tests;

public sealed class CycleAndReachabilityTests
{
    [Fact]
    public void Resolve_AllowsACycleBetweenSei()
    {
        using var fixture = new Fixture();
        fixture.AddSei("a", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("b", "[1.0.0]")],
            Artifacts = [new ArtifactFixture { Files = { ["a.txt"] = "a" } }],
        });
        fixture.AddSei("b", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("a", "[1.0.0]")],
            Artifacts = [new ArtifactFixture { Files = { ["b.txt"] = "b" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("a", "[1.0.0]"));

        var result = new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal(2, result.SeiInstalled);
    }

    [Fact]
    public void Resolve_AllowsOptionalPeerToBeMissing()
    {
        using var fixture = new Fixture();
        fixture.AddSei("a", new VersionFixture
        {
            Version = "1.0.0",
            OptionalPeers = [new DepFixture("absent", "[1.0.0]")],
            Artifacts = [new ArtifactFixture { Files = { ["a.txt"] = "a" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("a", "[1.0.0]"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("a", File.ReadAllText(Path.Combine(fixture.Root, "a.txt")));
    }

    [Fact]
    public void Resolve_ReclaimsUnreachableCycleAfterRemove()
    {
        using var fixture = new Fixture();
        fixture.AddSei("root", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("a", "[1.0.0]")],
            Artifacts = [new ArtifactFixture { Files = { ["root.txt"] = "root" } }],
        });
        fixture.AddSei("a", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("b", "[1.0.0]")],
            Artifacts = [new ArtifactFixture { Files = { ["a.txt"] = "a" } }],
        });
        fixture.AddSei("b", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("a", "[1.0.0]")],
            Artifacts = [new ArtifactFixture { Files = { ["b.txt"] = "b" } }],
        });
        fixture.AddSei("other", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["other.txt"] = "other" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("root", "[1.0.0]"), new SpecFixture("other", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        fixture.WriteConfig("win-x64", new SpecFixture("other", "[1.0.0]"));
        client.Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("other", Assert.Single(client.ReadLock(fixture.Root)!.Sei).Name);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "a.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "b.txt")));
    }

    [Fact]
    public void Resolve_KeepsACycleReachableFromAnotherRoot()
    {
        using var fixture = new Fixture();
        fixture.AddSei("a", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("b", "[1.0.0]")],
            Artifacts = [new ArtifactFixture { Files = { ["a.txt"] = "a" } }],
        });
        fixture.AddSei("b", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("a", "[1.0.0]")],
            Artifacts = [new ArtifactFixture { Files = { ["b.txt"] = "b" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("a", "[1.0.0]"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen);

        Assert.Equal(2, new SeikoClient().ReadLock(fixture.Root)!.Sei.Count);
    }
}
