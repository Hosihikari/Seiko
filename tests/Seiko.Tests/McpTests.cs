using System.Text.Json.Nodes;
using Seiko.McpServer;
using Xunit;

namespace Seiko.Tests;

public sealed class McpTests
{
    [Fact]
    public void ToolsList_DefaultIsReadOnly()
    {
        var server = new SeikoMcpServer(Path.GetTempPath(), allowMutations: false);
        var response = server.Handle("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var names = ToolNames(response);
        Assert.Contains("list", names);
        Assert.Contains("search", names);
        Assert.Contains("why", names);
        Assert.Contains("outdated", names);
        Assert.Contains("config_get", names);
        Assert.Contains("cache_status", names);
        Assert.Contains("cache_verify", names);
        Assert.DoesNotContain("sync", names);
        Assert.DoesNotContain("add", names);
        Assert.DoesNotContain("source_add", names);
    }

    [Fact]
    public void ToolsList_WithMutationsIncludesSync()
    {
        var server = new SeikoMcpServer(Path.GetTempPath(), allowMutations: true);
        var names = ToolNames(server.Handle("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!);
        Assert.Contains("sync", names);
        Assert.Contains("rollback", names);
        Assert.Contains("add", names);
        Assert.Contains("remove", names);
        Assert.Contains("update", names);
        Assert.Contains("source_add", names);
        Assert.Contains("source_remove", names);
        Assert.Contains("config_set", names);
        Assert.Contains("cache_clean", names);
    }

    [Fact]
    public void AddTool_WritesDeclarationAndInstalls()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64");
        var server = new SeikoMcpServer(fixture.Root, allowMutations: true);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 3,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "add",
                ["arguments"] = new JsonObject
                {
                    ["root"] = fixture.Root,
                    ["spec"] = "hello@[1.0.0]",
                    ["store"] = fixture.StoreDirectory,
                },
            },
        }.ToJsonString();

        var response = server.Handle(request)!;
        var result = JsonNode.Parse(response)!.AsObject()["result"]!.AsObject();

        Assert.False(result["isError"]!.GetValue<bool>());
        Assert.True(File.Exists(Path.Combine(fixture.Root, "hello.txt")));
        Assert.Contains("hello", File.ReadAllText(Path.Combine(fixture.Root, "seiko.conf")));
    }

    [Fact]
    public void MutationTool_RequiresExplicitRoot()
    {
        var server = new SeikoMcpServer(Path.GetTempPath(), allowMutations: true);
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
        var server = new SeikoMcpServer(Path.GetTempPath(), allowMutations: false);
        var response = server.Handle(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""")!;
        var result = JsonNode.Parse(response)!.AsObject()["result"]!.AsObject();
        Assert.Equal("2025-06-18", result["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]!.AsObject()["tools"]);
    }

    [Fact]
    public void Initialize_ReportsItsOwnVersionWhenTheClientRequestsAnother()
    {
        var server = new SeikoMcpServer(Path.GetTempPath(), allowMutations: false);
        var response = server.Handle(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2099-01-01"}}""")!;
        var result = JsonNode.Parse(response)!.AsObject()["result"]!.AsObject();

        Assert.Equal("2025-06-18", result["protocolVersion"]!.GetValue<string>());
    }

    private static List<string> ToolNames(string response)
    {
        var tools = JsonNode.Parse(response)!.AsObject()["result"]!.AsObject()["tools"]!.AsArray();
        return tools.Select(tool => tool!["name"]!.GetValue<string>()).ToList();
    }
}
