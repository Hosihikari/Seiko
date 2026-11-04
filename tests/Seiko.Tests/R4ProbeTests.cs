using System.Text.Json.Nodes;
using Xunit;

namespace Seiko.Tests;

public sealed class R4ProbeTests
{
    [Fact]
    public void F1a_LongResultsLeak()
    {
        using var fixture = new Fixture();
        var lines = new List<string>
        {
            $"schemaVersion = {SeikoConfig.CurrentSchemaVersion}",
            "sources = [",
            "  { id = \"first\", index = \"http://demo:密码SECRET_SENTINEL@127.0.0.1/index.json?token=QUERY_SENTINEL\" },",
        };
        for (var i = 0; i < 300; i++)
        {
            lines.Add($"  {{ id = \"filler{i}\", index = \"https://example.invalid/{new string('a', 20)}/index.json\" }},");
        }

        lines.Add("]");
        lines.Add("rid = \"win-x64\"");
        lines.Add("sei = []");
        File.WriteAllText(Path.Combine(fixture.Root, "seiko.conf"), string.Join('\n', lines));
        var audit = Path.Combine(fixture.Root, "audit.jsonl");
        var server = new Seiko.McpServer.SeikoMcpServer(fixture.Root, allowMutations: false, auditLog: audit);

        var r = server.Handle(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"source_list\",\"arguments\":{\"root\":\""
            + fixture.Root.Replace("\\", "\\\\") + "\"}}}");

        Assert.NotNull(r);
        var text = File.ReadAllText(audit);
        System.Console.WriteLine("LEN=" + text.Length);
        Assert.DoesNotContain("SECRET_SENTINEL", text);
        Assert.DoesNotContain("QUERY_SENTINEL", text);
    }

    [Fact]
    public void F1b_InvalidUrlWithUnicodeLeaks()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var audit = Path.Combine(fixture.Root, "audit.jsonl");
        var server = new Seiko.McpServer.SeikoMcpServer(fixture.Root, allowMutations: true, auditLog: audit);

        server.Handle(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"source_add\",\"arguments\":{\"root\":\""
            + fixture.Root.Replace("\\", "\\\\")
            + "\",\"id\":\"demo\",\"index\":\"http://demo:密码SECRET_SENTINEL@[\"}}}");

        var text = File.ReadAllText(audit);
        Assert.DoesNotContain("SECRET_SENTINEL", text);
    }

    [Fact]
    public void F2_UniqueMoreSpecificArtifactWinsRegardlessOfDeclarationOrder()
    {
        foreach (var markerFirst in new[] { true, false })
        {
            using var fixture = new Fixture();
            fixture.AddSei(
                "marker",
                new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["m1.txt"] = "1" } }] },
                new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture { Files = { ["m2.txt"] = "2" } }] });
            fixture.AddSei("aaa", new VersionFixture
            {
                Version = "1.0.0",
                Artifacts =
                [
                    new ArtifactFixture { Files = { ["a.txt"] = "plain" } },
                    new ArtifactFixture
                    {
                        Dependencies = [new DepFixture("marker", "[1.0.0]")],
                        Prerequisites = [new DepFixture("marker", "[1.0.0]")],
                        Files = { ["a.txt"] = "specific" },
                    },
                ],
            });
            SpecFixture[] specs = markerFirst
                ? [new SpecFixture("marker", "*"), new SpecFixture("aaa", "[1.0.0]")]
                : [new SpecFixture("aaa", "[1.0.0]"), new SpecFixture("marker", "*")];
            fixture.WriteConfig("win-x64", specs);

            new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

            Assert.Equal("specific", File.ReadAllText(Path.Combine(fixture.Root, "a.txt")));
        }
    }
}
