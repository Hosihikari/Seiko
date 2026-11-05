using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Seiko.Tests;

// The envelope pointer only moves for breaking changes: adding error.hint is
// additive, so the version stays 1.
public sealed class JsonContractTests
{
    [Fact]
    public void ErrorEnvelopeKeepsVersionOneAndCarriesAnOptionalHint()
    {
        var (stdout, _, exitCode) = Cli.Run(["info", "nope", "--json"]);

        Assert.NotEqual(0, exitCode);
        var node = JsonNode.Parse(stdout)!.AsObject();
        Assert.Equal(1, node["schemaVersion"]!.GetValue<int>());
        if (node["error"]!["hint"] is { } hint)
        {
            Assert.Equal(JsonValueKind.String, hint.GetValueKind());
        }
    }

    [Fact]
    public void SearchDataAdditionsKeepVersionOne()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteConfig("win-x64");

        var (stdout, _, _) = Cli.Run(["search", "hello", "--json", "--root", fixture.Root]);

        var node = JsonNode.Parse(stdout)!.AsObject();
        Assert.Equal(1, node["schemaVersion"]!.GetValue<int>());
    }
}
