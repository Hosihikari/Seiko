using Xunit;

namespace Seiko.Tests;

public sealed class SourceStatusTests
{
    private static Fixture Healthy()
    {
        var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.WriteConfig("win-x64");
        return fixture;
    }

    [Fact]
    public void Status_ReportsAReachableSourceWithACatalog()
    {
        using var fixture = Healthy();

        var status = Assert.Single(
            new SeikoClient().SourceStatus(fixture.Root, storeRoot: fixture.StoreDirectory));

        Assert.Equal("local", status.Id);
        Assert.True(status.Reachable);
        Assert.True(status.CatalogPresent);
        Assert.Equal(1, status.SeiCount);
        Assert.Empty(status.Problems);
    }

    [Fact]
    public void Status_ReportsAMissingCatalogWithoutFailing()
    {
        using var fixture = Healthy();
        File.Delete(Path.Combine(fixture.SourceDirectory, "catalog.json"));

        var status = Assert.Single(
            new SeikoClient().SourceStatus(fixture.Root, storeRoot: fixture.StoreDirectory));

        Assert.True(status.Reachable);
        Assert.False(status.CatalogPresent);
    }

    [Fact]
    public void Status_ReportsAnUnreachableSource()
    {
        using var fixture = Healthy();
        var indexPath = Path.Combine(fixture.SourceDirectory, "index.json");
        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            File.ReadAllText(Path.Combine(fixture.Root, "seiko.conf")).Replace(
                indexPath.Replace('\\', '/'),
                Path.Combine(fixture.Root, "missing", "index.json").Replace('\\', '/')));

        var status = Assert.Single(
            new SeikoClient().SourceStatus(fixture.Root, storeRoot: fixture.StoreDirectory));

        Assert.False(status.Reachable);
        Assert.NotEmpty(status.Problems);
    }

    [Fact]
    public void Status_FiltersToOneSourceAndRejectsUnknownIds()
    {
        using var fixture = Healthy();

        Assert.Single(new SeikoClient().SourceStatus(
            fixture.Root,
            "local",
            storeRoot: fixture.StoreDirectory));

        var error = Assert.Throws<SeikoException>(() => new SeikoClient().SourceStatus(
            fixture.Root,
            "nope",
            storeRoot: fixture.StoreDirectory));
        Assert.Equal("config", error.Code);
    }

    [Fact]
    public void Status_UsesTheCacheOffline()
    {
        using var fixture = Healthy();
        // Populate the metadata cache once, then drop the source directory.
        new SeikoClient().Search(fixture.Root, "hello", storeRoot: fixture.StoreDirectory);
        var cached = new SeikoClient();

        var status = Assert.Single(cached.SourceStatus(
            fixture.Root,
            offline: true,
            storeRoot: fixture.StoreDirectory));

        Assert.True(status.Reachable);
    }
}
