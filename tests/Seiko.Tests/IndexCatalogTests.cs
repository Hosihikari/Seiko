using System.Text.Json;
using Xunit;

namespace Seiko.Tests;

// `sk index catalog` is the private-source counterpart of the index bot: it
// writes the same deterministic file and --check fails CI when it is stale.
public sealed class IndexCatalogCommandTests
{
    private static Fixture FixtureWithSei()
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
    public void Catalog_IsDeterministic()
    {
        using var fixture = FixtureWithSei();
        File.Delete(Path.Combine(fixture.SourceDirectory, "catalog.json"));

        CatalogFile.Write(Path.Combine(fixture.Root, "a.json"), CatalogFile.Build(CatalogFile.Load(fixture.SourceDirectory)));
        CatalogFile.Write(Path.Combine(fixture.Root, "b.json"), CatalogFile.Build(CatalogFile.Load(fixture.SourceDirectory)));

        Assert.Equal(
            File.ReadAllText(Path.Combine(fixture.Root, "a.json")),
            File.ReadAllText(Path.Combine(fixture.Root, "b.json")));
    }

    [Fact]
    public void Verify_FailsWhenTheCatalogIsStale()
    {
        using var fixture = FixtureWithSei();
        var path = Path.Combine(fixture.SourceDirectory, "catalog.json");
        var catalog = CatalogFile.TryRead(path)!;
        catalog.Sei.Clear();
        CatalogFile.Write(path, catalog);

        var result = IndexVerifier.Verify(fixture.SourceDirectory);

        Assert.Contains(result.Findings, finding => finding.Code == "catalog-stale");
    }

    [Fact]
    public void Verify_WarnsWhenTheCatalogIsMissing()
    {
        using var fixture = FixtureWithSei();
        File.Delete(Path.Combine(fixture.SourceDirectory, "catalog.json"));

        var result = IndexVerifier.Verify(fixture.SourceDirectory);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("catalog-missing", finding.Code);
        Assert.Equal("warning", finding.Severity);
    }

    [Fact]
    public void Verify_AcceptsAMatchingCatalog()
    {
        using var fixture = FixtureWithSei();

        var result = IndexVerifier.Verify(fixture.SourceDirectory);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Load_SkipsUnreadableFiles()
    {
        using var fixture = FixtureWithSei();
        File.WriteAllText(Path.Combine(fixture.SourceDirectory, "sei", "broken.json"), "{");

        var loaded = CatalogFile.Load(fixture.SourceDirectory).ToList();

        Assert.Single(loaded);
        Assert.Equal("hello", loaded[0].Name);
    }
}
