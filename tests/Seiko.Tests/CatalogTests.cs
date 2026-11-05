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
    public void Search_CatalogAndFallbackChooseTheSameVersion()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "hello",
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] },
            new VersionFixture { Version = "2.0.0-beta", Artifacts = [new ArtifactFixture()] });
        fixture.WriteConfig("win-x64");

        var fallback = Assert.Single(new SeikoClient().Search(fixture.Root, "hello")).Version;
        WriteCatalog(fixture, Read(fixture, "hello"));
        var catalogued = Assert.Single(new SeikoClient().Search(fixture.Root, "hello")).Version;

        Assert.Equal("1.0.0", fallback);
        Assert.Equal(fallback, catalogued);
    }

    [Fact]
    public void OfflineSearch_KeepsEachNamesSource()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.Root);
        var first = Path.Combine(fixture.Root, "sources", "a");
        var second = Path.Combine(fixture.Root, "sources", "b");
        WriteSource(first, "hello");
        WriteSource(second, "world");
        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            "schemaVersion = 2\n"
            + "sources = [\n"
            + $"  {{ id = \"a\", index = \"{server.BaseUrl}sources/a/index.json\" }},\n"
            + $"  {{ id = \"b\", index = \"{server.BaseUrl}sources/b/index.json\" }}\n"
            + "]\n"
            + "rid = \"win-x64\"\n"
            + "sei = []\n");

        // Online caches each source's index and catalog, but neither source
        // lists the other's package, so no per-name file is fetched.
        var online = new SeikoClient().Search(fixture.Root, "o", storeRoot: fixture.StoreDirectory);
        Assert.Equal(2, online.Count);

        var offline = new SeikoClient().Search(
            fixture.Root,
            "o",
            offline: true,
            storeRoot: fixture.StoreDirectory);
        Assert.Equal(2, offline.Count);
    }

    [Fact]
    public void OfflineSearch_LocalCatalogAnswersBeforeLaterSources()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.Root);
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        WriteCatalog(fixture, Read(fixture, "hello"));
        File.Delete(Path.Combine(fixture.SourceDirectory, "sei", "hello.json"));
        File.WriteAllBytes(
            Path.Combine(fixture.SourceDirectory, "index.json"),
            JsonSerializer.SerializeToUtf8Bytes(
                new IndexRoot { Sei = ["hello"] },
                SeikoJsonContext.Default.IndexRoot));

        // The HTTP source lists the name but its catalog is empty; online
        // search never asks it because the local catalog already answered.
        var second = Path.Combine(fixture.Root, "sources", "b");
        Directory.CreateDirectory(second);
        File.WriteAllBytes(
            Path.Combine(second, "index.json"),
            JsonSerializer.SerializeToUtf8Bytes(
                new IndexRoot { Sei = ["hello"] },
                SeikoJsonContext.Default.IndexRoot));
        File.WriteAllBytes(
            Path.Combine(second, "catalog.json"),
            JsonSerializer.SerializeToUtf8Bytes(
                new SeiCatalog { Sei = [] },
                SeikoJsonContext.Default.SeiCatalog));

        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            "schemaVersion = 2\n"
            + "sources = [\n"
            + $"  {{ id = \"local\", index = \"{Path.Combine(fixture.SourceDirectory, "index.json").Replace('\\', '/')}\" }},\n"
            + $"  {{ id = \"b\", index = \"{server.BaseUrl}sources/b/index.json\" }}\n"
            + "]\n"
            + "rid = \"win-x64\"\n"
            + "sei = []\n");

        Assert.Single(new SeikoClient().Search(
            fixture.Root,
            "hello",
            storeRoot: fixture.StoreDirectory));

        var offline = Assert.Single(new SeikoClient().Search(
            fixture.Root,
            "hello",
            offline: true,
            storeRoot: fixture.StoreDirectory));
        Assert.Equal("hello", offline.Name);
    }

    private static void WriteSource(string directory, string name)
    {
        Directory.CreateDirectory(Path.Combine(directory, "sei"));
        File.WriteAllBytes(
            Path.Combine(directory, "index.json"),
            JsonSerializer.SerializeToUtf8Bytes(
                new IndexRoot { Sei = [name] },
                SeikoJsonContext.Default.IndexRoot));
        var catalog = CatalogFile.Build([
            new SeiIndex
            {
                Name = name,
                Project = new ProjectMetadata
                {
                    Description = $"{name} package",
                    Authors = ["fixture"],
                    License = "MIT",
                },
                Versions = [new SeiVersionEntry { Version = "1.0.0" }],
            },
        ]);
        File.WriteAllBytes(
            Path.Combine(directory, "catalog.json"),
            JsonSerializer.SerializeToUtf8Bytes(catalog, SeikoJsonContext.Default.SeiCatalog));
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
