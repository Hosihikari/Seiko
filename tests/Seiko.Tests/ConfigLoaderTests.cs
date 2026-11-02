using Xunit;

namespace Seiko.Tests;

public sealed class ConfigLoaderTests
{
    [Fact]
    public void Load_RejectsDuplicateSourceIds()
    {
        using var fixture = new Fixture();
        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            $$"""
            schemaVersion = {{SeikoConfig.CurrentSchemaVersion}}
            sources = [
              { id = "same", index = "https://example.invalid/a/index.json" },
              { id = "same", index = "https://example.invalid/b/index.json" }
            ]
            rid = "win-x64"
            sei = []
            """);

        var error = Assert.Throws<SeikoException>(() => ConfigLoader.Load(fixture.Root));

        Assert.Equal("config", error.Code);
        Assert.Contains("duplicate source id", error.Message);
        Assert.Contains("same", error.Message);
    }

    [Fact]
    public void Load_RejectsMissingSchemaVersion()
    {
        using var fixture = new Fixture();
        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            """
            sources = [
              { id = "local", index = "https://example.invalid/index.json" }
            ]
            sei = []
            """);

        var error = Assert.Throws<SeikoException>(() => ConfigLoader.Load(fixture.Root));

        Assert.Equal("config", error.Code);
        Assert.Contains("schemaVersion", error.Message);
    }

    [Fact]
    public void Load_RejectsEmptyComponentsSection()
    {
        using var fixture = new Fixture();
        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            $$"""
            schemaVersion = {{SeikoConfig.CurrentSchemaVersion}}
            sources = [
              { id = "local", index = "https://example.invalid/index.json" }
            ]
            sei = []
            components = []
            """);

        var error = Assert.Throws<SeikoException>(() => ConfigLoader.Load(fixture.Root));

        Assert.Equal("config", error.Code);
        Assert.Contains("components", error.Message);
    }

    [Fact]
    public void Load_RejectsComponentsSection()
    {
        using var fixture = new Fixture();
        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            $$"""
            schemaVersion = {{SeikoConfig.CurrentSchemaVersion}}
            sources = [
              { id = "local", index = "https://example.invalid/index.json" }
            ]
            sei = []
            components = [
              { name = "hello", version = "*" }
            ]
            """);

        var error = Assert.Throws<SeikoException>(() => ConfigLoader.Load(fixture.Root));

        Assert.Equal("config", error.Code);
        Assert.Contains("components", error.Message);
    }
}
