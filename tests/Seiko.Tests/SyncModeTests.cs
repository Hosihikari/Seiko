using Xunit;

namespace Seiko.Tests;

public sealed class SyncModeTests
{
    [Fact]
    public void Sync_FrozenRequiresLock()
    {
        using var fixture = new Fixture();
        fixture.AddPackage("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));

        var error = Assert.Throws<SeikoException>(() =>
            new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen));

        Assert.Contains("--frozen", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sync_FrozenRejectsNewDirectPackage()
    {
        using var fixture = new Fixture();
        fixture.AddPackage("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var engine = new SeikoClient();
        engine.Sync(fixture.Root, fixture.StoreDirectory);

        fixture.AddPackage("other", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["other.txt"] = "other" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"), new SpecFixture("other", "[1.0.0]"));

        var error = Assert.Throws<SeikoException>(() =>
            engine.Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen));
        Assert.Contains("other", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sync_FrozenReplaysLockedSet()
    {
        using var fixture = new Fixture();
        fixture.AddPackage("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var engine = new SeikoClient();
        engine.Sync(fixture.Root, fixture.StoreDirectory);

        fixture.WriteConfig("win-x64");
        var result = engine.Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen);

        Assert.Equal(1, result.SeiInstalled);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Sync_LockedRejectsStaleLock()
    {
        using var fixture = new Fixture();
        fixture.AddPackage("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var engine = new SeikoClient();
        engine.Sync(fixture.Root, fixture.StoreDirectory);

        fixture.AddPackage("other", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["other.txt"] = "other" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"), new SpecFixture("other", "[1.0.0]"));

        var error = Assert.Throws<SeikoException>(() =>
            engine.Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Locked));
        Assert.Contains("--locked", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sync_LockedAcceptsMatchingLock()
    {
        using var fixture = new Fixture();
        fixture.AddPackage("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var engine = new SeikoClient();
        engine.Sync(fixture.Root, fixture.StoreDirectory);

        var result = engine.Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Locked);

        Assert.Equal(1, result.SeiInstalled);
    }

    [Fact]
    public void Sync_FrozenRejectsUnsatisfiedRange()
    {
        using var fixture = new Fixture();
        fixture.AddPackage("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var engine = new SeikoClient();
        engine.Sync(fixture.Root, fixture.StoreDirectory);

        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));

        var error = Assert.Throws<SeikoException>(() =>
            engine.Sync(fixture.Root, fixture.StoreDirectory, mode: SyncMode.Frozen));
        Assert.Contains("does not satisfy", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
