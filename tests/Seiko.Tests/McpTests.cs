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

    [Fact]
    public void MutationRoot_OutsideAllowlistIsRejected()
    {
        var allowed = NewDirectory();
        var outside = NewDirectory();
        try
        {
            var server = new SeikoMcpServer(allowed, allowMutations: true, allowedRoots: [allowed]);

            var result = CallTool(server, "sync", new JsonObject { ["root"] = outside });

            Assert.True(result["isError"]!.GetValue<bool>());
            Assert.Contains("outside --allowed-root", ToolText(result));
        }
        finally
        {
            Directory.Delete(allowed, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void MutationRoot_InsideAllowedDirectoryIsAccepted()
    {
        var allowed = NewDirectory();
        var nested = Path.Combine(allowed, "nested");
        Directory.CreateDirectory(nested);
        try
        {
            var server = new SeikoMcpServer(allowed, allowMutations: true, allowedRoots: [allowed]);

            var result = CallTool(server, "sync", new JsonObject { ["root"] = nested });

            Assert.True(result["isError"]!.GetValue<bool>());
            Assert.DoesNotContain("allowed-root", ToolText(result));
        }
        finally
        {
            Directory.Delete(allowed, recursive: true);
        }
    }

    [Fact]
    public void ReadOnlyRoot_IsNotRestrictedByAllowlist()
    {
        var allowed = NewDirectory();
        var outside = NewDirectory();
        try
        {
            var server = new SeikoMcpServer(allowed, allowMutations: false, allowedRoots: [allowed]);

            var result = CallTool(server, "list", new JsonObject { ["root"] = outside });

            Assert.False(result["isError"]!.GetValue<bool>());
        }
        finally
        {
            Directory.Delete(allowed, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void MutationStore_OutsideAllowlistIsRejected()
    {
        var allowed = NewDirectory();
        var store = NewDirectory();
        var outside = NewDirectory();
        try
        {
            var server = new SeikoMcpServer(
                allowed,
                allowMutations: true,
                allowedRoots: [allowed],
                allowedStores: [store]);

            var result = CallTool(
                server,
                "sync",
                new JsonObject { ["root"] = allowed, ["store"] = outside });

            Assert.True(result["isError"]!.GetValue<bool>());
            Assert.Contains("outside --allowed-store", ToolText(result));
        }
        finally
        {
            Directory.Delete(allowed, recursive: true);
            Directory.Delete(store, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "seiko-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static JsonObject CallTool(SeikoMcpServer server, string name, JsonObject arguments)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = name,
                ["arguments"] = arguments,
            },
        }.ToJsonString();
        return JsonNode.Parse(server.Handle(request)!)!.AsObject()["result"]!.AsObject();
    }

    private static string ToolText(JsonObject result)
        => result["content"]!.AsArray()[0]!.AsObject()["text"]!.GetValue<string>();

    private static List<string> ToolNames(string response)
    {
        var tools = JsonNode.Parse(response)!.AsObject()["result"]!.AsObject()["tools"]!.AsArray();
        return tools.Select(tool => tool!["name"]!.GetValue<string>()).ToList();
    }
}
