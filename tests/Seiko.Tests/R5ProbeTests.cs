using System.Text.Json.Nodes;
using Xunit;

namespace Seiko.Tests;

public sealed class R5ProbeTests
{
    [Fact]
    public void F1_SingleQuoteInUnparsableUrlLeaks()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var audit = Path.Combine(fixture.Root, "audit.jsonl");
        var server = new Seiko.McpServer.SeikoMcpServer(fixture.Root, allowMutations: true, auditLog: audit);

        server.Handle(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"source_add\",\"arguments\":{\"root\":\""
            + fixture.Root.Replace("\\", "\\\\")
            + "\",\"id\":\"demo\",\"index\":\"http://demo:part'SECRET_SENTINEL@[\"}}}");

        var text = File.ReadAllText(audit);
        Assert.DoesNotContain("SECRET_SENTINEL", text);
    }

    [Fact]
    public void F2_TruncatedAuditLineIsValidJson()
    {
        using var fixture = new Fixture();
        var lines = new List<string>
        {
            $"schemaVersion = {SeikoConfig.CurrentSchemaVersion}",
            "sources = [",
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

        server.Handle(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"source_list\",\"arguments\":{\"root\":\""
            + fixture.Root.Replace("\\", "\\\\") + "\"}}}");

        foreach (var line in File.ReadAllLines(audit))
        {
            var parsed = JsonNode.Parse(line);
            Assert.NotNull(parsed);
            Assert.Contains("[truncated", parsed!["result"]!.GetValue<string>());
        }
    }
}
