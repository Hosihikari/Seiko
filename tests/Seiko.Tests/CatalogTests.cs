using System.Text.Json;
using Xunit;

namespace Seiko.Tests;

public sealed class CatalogTests
{
    private static void WriteCatalog(Fixture fixture, params SeiIndex[] indexes)
        => File.WriteAllBytes(
            Path.Combine(fixture.SourceDirectory, "catalog.json"),
            JsonSerializer.SerializeToUtf8Bytes(
                CatalogFile.Build(indexes),
                SeikoJsonContext.Default.SeiCatalog));

    private static SeiIndex Read(Fixture fixture, string name)
        => JsonSerializer.Deserialize(
            File.ReadAllBytes(Path.Combine(fixture.SourceDirectory, "sei", name + ".json")),
            SeikoJsonContext.Default.SeiIndex)!;

    [Fact]
    public void Search_UsesTheCatalogAndSkipsPerNameFiles()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.WriteConfig("win-x64");
        WriteCatalog(fixture, Read(fixture, "hello"));

        // Remove the per-name file: only the catalog can answer now.
        File.Delete(Path.Combine(fixture.SourceDirectory, "sei", "hello.json"));

        var result = Assert.Single(new SeikoClient().Search(fixture.Root, "hello"));
        Assert.Equal("hello", result.Name);
        Assert.Equal("1.0.0", result.Version);
    }

    [Fact]
    public void OfflineSearch_UsesTheCachedCatalogWithoutPerNameFiles()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        WriteCatalog(fixture, Read(fixture, "hello"));
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");

        // The catalog answers the search, so the per-name file is never
        // fetched or cached.
        Assert.Single(new SeikoClient().Search(
            fixture.Root,
            "hello",
            storeRoot: fixture.StoreDirectory));

        var result = Assert.Single(new SeikoClient().Search(
            fixture.Root,
            "hello",
            offline: true,
            storeRoot: fixture.StoreDirectory));
        Assert.Equal("hello", result.Name);
    }

    [Fact]
    public void Search_MatchesDescriptionAndTagsFromTheCatalog()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.WriteConfig("win-x64");
        var sei = Read(fixture, "hello");
        sei.Project = new ProjectMetadata
        {
            Description = "a greeting service",
            Authors = ["a"],
            License = "Apache-2.0",
            Tags = ["greeting"],
        };
        WriteCatalog(fixture, sei);
        File.Delete(Path.Combine(fixture.SourceDirectory, "sei", "hello.json"));

        Assert.Single(new SeikoClient().Search(fixture.Root, "service"));
        Assert.Single(new SeikoClient().Search(fixture.Root, "greeting"));

        var byTag = Assert.Single(new SeikoClient().Search(fixture.Root, "hello", tag: "greeting"));
        Assert.Equal("Apache-2.0", byTag.License);
        Assert.Empty(new SeikoClient().Search(fixture.Root, "hello", tag: "other"));
        Assert.Empty(new SeikoClient().Search(fixture.Root, "hello", license: "MIT"));
    }

    [Fact]
    public void Search_FallsBackWhenTheCatalogHasDuplicateNames()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.WriteConfig("win-x64");
        var entry = new CatalogEntry { Name = "hello", Version = "1.0.0" };
        File.WriteAllBytes(
            Path.Combine(fixture.SourceDirectory, "catalog.json"),
            JsonSerializer.SerializeToUtf8Bytes(
                new SeiCatalog { Sei = [entry, entry] },
                SeikoJsonContext.Default.SeiCatalog));

        var result = Assert.Single(new SeikoClient().Search(fixture.Root, "hello"));

        Assert.Equal("hello", result.Name);
    }

    [Fact]
    public void Search_FallsBackWhenTheCatalogHasANullEntry()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.WriteConfig("win-x64");
        File.WriteAllBytes(
            Path.Combine(fixture.SourceDirectory, "catalog.json"),
            JsonSerializer.SerializeToUtf8Bytes(
                new SeiCatalog { Sei = [null!] },
                SeikoJsonContext.Default.SeiCatalog));

        var result = Assert.Single(new SeikoClient().Search(fixture.Root, "hello"));

        Assert.Equal("hello", result.Name);
    }

    [Fact]
    public void Search_FallsBackWhenTheHttpCatalogHasDuplicateNames()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64");
        var entry = new CatalogEntry { Name = "hello", Version = "1.0.0" };
        File.WriteAllBytes(
            Path.Combine(fixture.SourceDirectory, "catalog.json"),
            JsonSerializer.SerializeToUtf8Bytes(
                new SeiCatalog { Sei = [entry, entry] },
                SeikoJsonContext.Default.SeiCatalog));

        var result = Assert.Single(new SeikoClient().Search(
            fixture.Root,
            "hello",
            storeRoot: fixture.StoreDirectory));

        Assert.Equal("hello", result.Name);
    }

    [Fact]
    public void Search_FallsBackWhenTheCatalogIsBroken()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.WriteConfig("win-x64");
        File.WriteAllText(Path.Combine(fixture.SourceDirectory, "catalog.json"), "{ not json");

        var result = Assert.Single(new SeikoClient().Search(fixture.Root, "hello"));
        Assert.Equal("hello", result.Name);
    }

    [Fact]
    public void Search_MatchesNamesOutsideTheCatalog()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.AddSei("other", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.WriteConfig("win-x64");
        WriteCatalog(fixture, Read(fixture, "hello"));

        var result = Assert.Single(new SeikoClient().Search(fixture.Root, "other"));
        Assert.Equal("other", result.Name);
    }

    [Fact]
    public void Search_SkipsUnlistedFromTheCatalog()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.WriteConfig("win-x64");
        var sei = Read(fixture, "hello");
        sei.Unlisted = true;
        WriteCatalog(fixture, sei);
        File.Delete(Path.Combine(fixture.SourceDirectory, "sei", "hello.json"));

        Assert.Empty(new SeikoClient().Search(fixture.Root, "hello"));
    }

    [Fact]
    public void Search_RespectsTheLimit()
    {
        using var fixture = new Fixture();
        fixture.AddSei("alpha", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.AddSei("alpine", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteConfig("win-x64");
        WriteCatalog(fixture, Read(fixture, "alpha"), Read(fixture, "alpine"));

        var results = new SeikoClient().Search(fixture.Root, "al", limit: 1);

        Assert.Single(results);
    }

    [Fact]
    public void Build_PicksTheLatestUnyankedVersionAndTheMostSevereAdvisory()
    {
        var index = new SeiIndex
        {
            Name = "hello",
            Project = new ProjectMetadata { Description = "d", Authors = ["a"], License = "MIT" },
            Versions =
            [
                new SeiVersionEntry
                {
                    Version = "1.0.0",
                    Advisory = new AdvisoryInfo { Severity = "low" },
                },
                new SeiVersionEntry
                {
                    Version = "2.0.0",
                    Advisory = new AdvisoryInfo { Severity = "critical" },
                },
                new SeiVersionEntry { Version = "3.0.0", Yanked = true },
            ],
        };

        var entry = Assert.Single(CatalogFile.Build([index]).Sei);

        // Critical versions are skipped like yanked ones; the advisory column
        // still reports the most severe finding in the package.
        Assert.Equal("1.0.0", entry.Version);
        Assert.Equal("critical", entry.Advisory!.Severity);
    }
}
