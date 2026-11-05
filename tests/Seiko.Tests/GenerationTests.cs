using System.Text.Json.Nodes;
using Xunit;

namespace Seiko.Tests;

// A destructive command needs a history and a checkpoint: `generation list`
// shows what exists, `rollback --dry-run` shows what the rollback would do,
// and neither changes the environment.
public sealed class GenerationTests
{
    private static Fixture Installed(string content = "one")
    {
        var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = content } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        return fixture;
    }

    private static void Upgrade(Fixture fixture)
    {
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "two" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
    }

    [Fact]
    public void Generations_ListIsNewestFirstAndMarksTheCurrentOne()
    {
        using var fixture = Installed();
        Upgrade(fixture);

        var generations = new SeikoClient().Generations(fixture.Root, fixture.StoreDirectory);

        Assert.Equal(2, generations.Count);
        Assert.True(generations[0].Current);
        Assert.False(generations[1].Current);
        Assert.All(generations, item => Assert.NotNull(item.CreatedAt));
        Assert.All(generations, item => Assert.True(item.Valid));
    }

    [Fact]
    public void Generations_ReportsAnIncompleteSnapshotWithoutFailing()
    {
        using var fixture = Installed();
        var generation = new SeikoClient().Generations(fixture.Root, fixture.StoreDirectory).Single();
        File.Delete(Path.Combine(
            fixture.Root,
            ".seiko",
            "generations",
            generation.Generation.ToString(),
            "seiko.conf"));

        var generations = new SeikoClient().Generations(fixture.Root, fixture.StoreDirectory);

        Assert.False(Assert.Single(generations).Valid);
    }

    [Fact]
    public void Generations_MarksAnUnsupportedLockInvalid()
    {
        using var fixture = Installed();
        Upgrade(fixture);
        var path = Path.Combine(fixture.Root, ".seiko", "generations", "1", "lock.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        node["schemaVersion"] = 999;
        File.WriteAllText(path, node.ToJsonString());

        var generations = new SeikoClient().Generations(fixture.Root, fixture.StoreDirectory);

        var broken = generations.Single(item => item.Generation == 1);
        Assert.False(broken.Valid);
        Assert.Equal(0, broken.SeiCount);
        Assert.True(generations.Single(item => item.Generation == 2).Valid);
    }

    [Fact]
    public void Generations_ShowsUnknownTimeForOldSnapshots()
    {
        using var fixture = Installed();
        var generation = new SeikoClient().Generations(fixture.Root, fixture.StoreDirectory).Single();
        File.Delete(Path.Combine(
            fixture.Root,
            ".seiko",
            "generations",
            generation.Generation.ToString(),
            "meta.json"));

        var generations = new SeikoClient().Generations(fixture.Root, fixture.StoreDirectory);

        Assert.Null(Assert.Single(generations).CreatedAt);
    }

    [Fact]
    public void Preview_DescribesTheRevertedFilesWithoutTouchingThem()
    {
        using var fixture = Installed();
        Upgrade(fixture);
        var before = File.ReadAllText(Path.Combine(fixture.Root, "hello.txt"));

        var preview = new SeikoClient().PreviewRollback(fixture.Root, storeRoot: fixture.StoreDirectory);

        Assert.Equal(2, preview.FromGeneration);
        Assert.Equal(1, preview.ToGeneration);
        Assert.Contains(preview.LockChanges, change => change.Contains("hello 2.0.0 -> 1.0.0"));
        Assert.Contains(preview.ConfigChanges, change => change.StartsWith("- config:", StringComparison.Ordinal));
        Assert.Equal("two", File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
        Assert.Equal(before, File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void Preview_ReportsFilesTheRollbackWouldRemove()
    {
        using var fixture = Installed();
        Upgrade(fixture);

        var preview = new SeikoClient().PreviewRollback(fixture.Root, storeRoot: fixture.StoreDirectory);

        // hello.txt exists in both, so it is replaced rather than added/removed.
        Assert.Contains("hello.txt", preview.Replaced);
        Assert.Empty(preview.Added);
        Assert.Empty(preview.Removed);
    }

    [Fact]
    public void Preview_FailsOnAnIncompleteTarget()
    {
        using var fixture = Installed();
        Upgrade(fixture);
        File.Delete(Path.Combine(fixture.Root, ".seiko", "generations", "1", "lock.json"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().PreviewRollback(fixture.Root, storeRoot: fixture.StoreDirectory));

        Assert.Equal("lock", error.Code);
    }

    [Fact]
    public void Preview_FailsWhenThereIsNoPreviousGeneration()
    {
        using var fixture = Installed();

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().PreviewRollback(fixture.Root, storeRoot: fixture.StoreDirectory));

        Assert.Equal("lock", error.Code);
        Assert.Contains("no previous generation", error.Message);
    }
}
