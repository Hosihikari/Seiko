using System.Text.Json.Nodes;
using Xunit;

namespace Seiko.Tests;

public sealed class McpTests
{
    [Fact]
    public void ToolsList_DefaultIsReadOnly()
    {
        var server = new Seiko.McpServer.McpServer(Path.GetTempPath(), allowMutations: false);
        var response = server.Handle("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var names = ToolNames(response);
        Assert.Contains("list", names);
        Assert.Contains("search", names);
        Assert.DoesNotContain("sync", names);
    }

    [Fact]
    public void ToolsList_WithMutationsIncludesSync()
    {
        var server = new Seiko.McpServer.McpServer(Path.GetTempPath(), allowMutations: true);
        var names = ToolNames(server.Handle("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!);
        Assert.Contains("sync", names);
        Assert.Contains("rollback", names);
    }

    [Fact]
    public void MutationTool_RequiresExplicitRoot()
    {
        var server = new Seiko.McpServer.McpServer(Path.GetTempPath(), allowMutations: true);
        var response = server.Handle(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"sync","arguments":{}}}""")!;
        var result = JsonNode.Parse(response)!.AsObject()["result"]!.AsObject();
        Assert.True(result["isError"]!.GetValue<bool>());
        var text = result["content"]!.AsArray()[0]!.AsObject()["text"]!.GetValue<string>();
        Assert.Contains("root is required", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Initialize_ReturnsToolsCapability()
    {
        var server = new Seiko.McpServer.McpServer(Path.GetTempPath(), allowMutations: false);
        var response = server.Handle(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""")!;
        var result = JsonNode.Parse(response)!.AsObject()["result"]!.AsObject();
        Assert.Equal("2025-06-18", result["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]!.AsObject()["tools"]);
    }

    private static List<string> ToolNames(string response)
    {
        var tools = JsonNode.Parse(response)!.AsObject()["result"]!.AsObject()["tools"]!.AsArray();
        return tools.Select(tool => tool!["name"]!.GetValue<string>()).ToList();
    }
}
