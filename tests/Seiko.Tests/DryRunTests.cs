using Xunit;

namespace Seiko.Tests;

public sealed class DryRunTests
{
    [Fact]
    public void DryRun_AddDoesNotWriteConfigLockOrDeployment()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64");
        var configBefore = File.ReadAllBytes(Path.Combine(fixture.Root, "seiko.conf"));

        var result = new SeikoClient().Add(
            fixture.Root,
            "hello",
            storeRoot: fixture.StoreDirectory,
            dryRun: true);

        Assert.NotNull(result.Preview);
        var change = Assert.Single(result.Preview.Changes);
        Assert.Equal("add", change.Action);
        Assert.Equal("hello", change.Name);
        Assert.Equal(configBefore, File.ReadAllBytes(Path.Combine(fixture.Root, "seiko.conf")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "seiko.lock.json")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "hello.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.StoreDirectory, "????????????????????????????????????????????????????????????????"));
    }

    [Fact]
    public void DryRun_RemoveReportsRemovedSei()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);

        var result = client.Remove(fixture.Root, "hello", storeRoot: fixture.StoreDirectory, dryRun: true);

        Assert.Contains(result.Preview!.Changes, change =>
            change.Action == "remove" && change.Name == "hello");
        Assert.Single(ConfigLoader.Load(fixture.Root).Sei);
        Assert.NotNull(client.ReadLock(fixture.Root));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void DryRun_LockedKeepsLockBytes()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var lockPath = Path.Combine(fixture.Root, "seiko.lock.json");
        var before = File.ReadAllBytes(lockPath);

        var result = client.Sync(
            fixture.Root,
            fixture.StoreDirectory,
            mode: SyncMode.Locked,
            dryRun: true);

        Assert.Equal("replay", Assert.Single(result.Preview!.Changes).Action);
        Assert.Equal(before, File.ReadAllBytes(lockPath));
    }

    [Fact]
    public void DryRun_ReportsRelationDiagnostics()
    {
        using var fixture = new Fixture();
        fixture.AddSei("dep", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["dep.txt"] = "dep" } }],
        });
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("dep", "[1.0.0]")],
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));

        var result = new SeikoClient().Sync(
            fixture.Root,
            fixture.StoreDirectory,
            dryRun: true);

        Assert.Contains(result.Preview!.Diagnostics, diagnostic =>
            diagnostic.Name == "hello"
            && diagnostic.Kind == "dependency"
            && diagnostic.Message.Contains("dep"));
        Assert.Contains(result.Preview.Diagnostics, diagnostic =>
            diagnostic.Kind == "artifact");
    }

    [Fact]
    public void DryRun_ReportsBlockedNewerVersions()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "hello",
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "one" } }],
            },
            new VersionFixture
            {
                Version = "2.0.0",
                Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "two" } }],
            });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));

        var result = new SeikoClient().Sync(
            fixture.Root,
            fixture.StoreDirectory,
            dryRun: true);

        Assert.Contains(result.Preview!.Blocked, block =>
            block.Code == "newer-version" && block.Message.Contains("2.0.0"));
        Assert.Equal(0, result.FilesDeployed);
    }
}
