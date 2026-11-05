using System.Text.Json;
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
        Assert.Contains("index_verify", names);
        Assert.Contains("doctor", names);
        Assert.Contains("check", names);
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
        Assert.Contains("init", names);
    }

    [Fact]
    public void ToolSurface_MatchesTheCliCommands()
    {
        var server = new SeikoMcpServer(Path.GetTempPath(), allowMutations: true);
        var names = ToolNames(server.Handle("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!);
        string[] expectedRead =
        [
            "list", "search", "info", "lock", "source_list", "config_get",
            "cache_status", "cache_verify", "why", "outdated", "index_verify", "doctor", "check",
        ];
        string[] expectedWrite =
        [
            "add", "remove", "update", "source_add", "source_remove",
            "config_set", "cache_clean", "sync", "rollback", "init",
        ];

        Assert.Equal(
            expectedRead.Concat(expectedWrite).OrderBy(name => name, StringComparer.Ordinal),
            names.OrderBy(name => name, StringComparer.Ordinal));
        Assert.DoesNotContain("install", names);
    }

    [Fact]
    public void RejectsInvalidJsonRpcEnvelopes()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var server = new SeikoMcpServer(fixture.Root, allowMutations: true);

        foreach (var request in new[]
        {
            """{"jsonrpc":"1.0","id":1,"method":"ping"}""",
            """{"id":1,"method":"ping"}""",
            """{"jsonrpc":2,"id":1,"method":"ping"}""",
        })
        {
            var response = JsonNode.Parse(server.Handle(request)!)!.AsObject();
            Assert.Equal(-32600, response["error"]!["code"]!.GetValue<int>());
        }

        var badId = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = new JsonObject { ["bad"] = true },
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "config_set",
                ["arguments"] = new JsonObject
                {
                    ["root"] = fixture.Root,
                    ["key"] = "rid",
                    ["value"] = "linux-x64",
                },
            },
        }.ToJsonString();
        var badIdResponse = JsonNode.Parse(server.Handle(badId)!)!.AsObject();
        Assert.Equal(-32600, badIdResponse["error"]!["code"]!.GetValue<int>());
        Assert.Equal("win-x64", ConfigLoader.Load(fixture.Root).Rid);
    }

    [Fact]
    public void SyncSchema_ExposesJobsAndProxy()
    {
        var server = new SeikoMcpServer(Path.GetTempPath(), allowMutations: true);
        var response = server.Handle("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var tools = JsonNode.Parse(response)!.AsObject()["result"]!.AsObject()["tools"]!.AsArray();
        var sync = Assert.Single(tools, tool => tool!["name"]!.GetValue<string>() == "sync")!;
        var properties = sync["inputSchema"]!["properties"]!.AsObject();

        Assert.True(properties.ContainsKey("jobs"));
        Assert.True(properties.ContainsKey("proxy"));
    }

    [Fact]
    public void SourceList_AllowsAnEmptySourceList()
    {
        using var fixture = new Fixture();
        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            $"schemaVersion = {SeikoConfig.CurrentSchemaVersion}\nsources = []\nrid = \"win-x64\"\nsei = []\n");
        var server = new SeikoMcpServer(fixture.Root, allowMutations: false);

        var result = CallTool(
            server,
            "source_list",
            new JsonObject { ["root"] = fixture.Root });

        Assert.False(result["isError"]!.GetValue<bool>());
        Assert.Equal("[]", ToolText(result));
    }

    [Fact]
    public void Search_ReturnsProjectFieldsAndFilters()
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
        var index = JsonSerializer.Deserialize<SeiIndex>(
            File.ReadAllBytes(Path.Combine(fixture.SourceDirectory, "sei", "hello.json")),
            SeikoJsonContext.Default.SeiIndex)!;
        index.Project = new ProjectMetadata
        {
            Description = "a greeting",
            Authors = ["someone"],
            License = "MIT",
            Tags = ["greeting"],
        };
        File.WriteAllBytes(
            Path.Combine(fixture.SourceDirectory, "sei", "hello.json"),
            JsonSerializer.SerializeToUtf8Bytes(index, SeikoJsonContext.Default.SeiIndex));
        var server = new SeikoMcpServer(fixture.Root, allowMutations: false);

        var result = CallTool(
            server,
            "search",
            new JsonObject { ["root"] = fixture.Root, ["term"] = "greet", ["tag"] = "greeting" });

        Assert.False(result["isError"]!.GetValue<bool>());
        var rows = JsonNode.Parse(ToolText(result))!.AsArray();
        var row = Assert.Single(rows)!.AsObject();
        Assert.Equal("hello", row["name"]!.GetValue<string>());
        Assert.Equal("a greeting", row["description"]!.GetValue<string>());
        Assert.Equal("MIT", row["license"]!.GetValue<string>());
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
    public void AuditLog_RedactsUrlSecrets()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var audit = Path.Combine(fixture.Root, "audit.jsonl");
        var server = new SeikoMcpServer(fixture.Root, allowMutations: true, auditLog: audit);

        CallTool(
            server,
            "source_add",
            new JsonObject
            {
                ["root"] = fixture.Root,
                ["id"] = "demo",
                ["index"] = "https://demo:SECRET@example.invalid/index.json?token=QUERY",
            });

        var text = File.ReadAllText(audit);
        Assert.DoesNotContain("SECRET", text);
        Assert.DoesNotContain("QUERY", text);
        Assert.Contains("https://example.invalid/index.json", text);
    }

    [Fact]
    public void AuditLog_RedactsSecretsWithUrlLegalSeparators()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var audit = Path.Combine(fixture.Root, "audit.jsonl");
        var server = new SeikoMcpServer(fixture.Root, allowMutations: true, auditLog: audit);

        CallTool(
            server,
            "source_add",
            new JsonObject
            {
                ["root"] = fixture.Root,
                ["id"] = "demo",
                ["index"] =
                    "http://demo:PASS,SECRET@127.0.0.1/index.json?token=QUERY",
            });

        var text = File.ReadAllText(audit);
        Assert.DoesNotContain("PASS", text);
        Assert.DoesNotContain("SECRET", text);
        Assert.DoesNotContain("QUERY", text);
        Assert.Contains("http://127.0.0.1/index.json", text);
    }

    [Fact]
    public void DoctorCleanNeedsMutationsButPlainDoctorDoesNot()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var server = new SeikoMcpServer(fixture.Root, allowMutations: false);

        var clean = CallTool(server, "doctor", new JsonObject { ["root"] = fixture.Root, ["clean"] = true });
        Assert.True(clean["isError"]!.GetValue<bool>());
        Assert.Contains("--allow-mutations", ToolText(clean));

        var plain = CallTool(server, "doctor", new JsonObject { ["root"] = fixture.Root });
        Assert.False(plain["isError"]!.GetValue<bool>());
    }

    [Fact]
    public void AuditLog_RejectsADirectoryPathAtStartup()
    {
        using var fixture = new Fixture();

        var error = Assert.Throws<SeikoException>(
            () => new SeikoMcpServer(fixture.Root, allowMutations: false, auditLog: fixture.Root));

        Assert.Equal("usage", error.Code);
    }

    [Fact]
    public void AuditLog_CreatesTheMissingDirectory()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var audit = Path.Combine(fixture.Root, "nested", "audit", "calls.jsonl");
        var server = new SeikoMcpServer(fixture.Root, allowMutations: false, auditLog: audit);

        CallTool(server, "check", new JsonObject { ["root"] = fixture.Root });

        Assert.True(File.Exists(audit));
    }

    [Fact]
    public void AuditLog_RecordsEachCall()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var audit = Path.Combine(fixture.Root, "audit", "calls.jsonl");
        var server = new SeikoMcpServer(fixture.Root, allowMutations: false, auditLog: audit);

        CallTool(server, "check", new JsonObject { ["root"] = fixture.Root });
        CallTool(server, "nope", new JsonObject());

        var lines = File.ReadAllLines(audit);
        Assert.Equal(2, lines.Length);
        var first = JsonNode.Parse(lines[0])!.AsObject();
        Assert.Equal("check", first["tool"]!.GetValue<string>());
        Assert.Equal("ok", first["outcome"]!.GetValue<string>());
        var second = JsonNode.Parse(lines[1])!.AsObject();
        Assert.Equal("error", second["outcome"]!.GetValue<string>());
    }

    [Fact]
    public void DoctorAndCheckAreAvailableWithoutMutations()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var server = new SeikoMcpServer(fixture.Root, allowMutations: false);

        foreach (var tool in new[] { "doctor", "check" })
        {
            var result = CallTool(server, tool, new JsonObject { ["root"] = fixture.Root });
            Assert.False(result["isError"]!.GetValue<bool>());
            Assert.True(JsonNode.Parse(ToolText(result))!.AsObject().ContainsKey("findings"));
        }
    }

    [Fact]
    public void IndexVerifyTool_ReportsFindings()
    {
        using var fixture = new Fixture();
        var server = new SeikoMcpServer(fixture.Root, allowMutations: false);

        var result = CallTool(
            server,
            "index_verify",
            new JsonObject { ["path"] = Path.Combine(fixture.SourceDirectory, "missing") });

        Assert.False(result["isError"]!.GetValue<bool>());
        var data = JsonNode.Parse(ToolText(result))!.AsObject();
        Assert.Equal(1, data["errors"]!.GetValue<int>());
        Assert.True(data["failed"]!.GetValue<bool>());
    }

    [Fact]
    public void AddTool_DryRunReturnsChangesWithoutWriting()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64");
        var server = new SeikoMcpServer(fixture.Root, allowMutations: true);

        var result = CallTool(
            server,
            "add",
            new JsonObject
            {
                ["root"] = fixture.Root,
                ["spec"] = "hello",
                ["store"] = fixture.StoreDirectory,
                ["dryRun"] = true,
            });

        Assert.False(result["isError"]!.GetValue<bool>());
        var data = JsonNode.Parse(ToolText(result))!.AsObject();
        Assert.Equal("add", data["changes"]![0]!["action"]!.GetValue<string>());
        Assert.False(data["downloaded"]!.GetValue<bool>());
        Assert.Empty(ConfigLoader.Load(fixture.Root).Sei);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public void OutdatedTool_IncludesBlockers()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "one" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "two" } }],
        });
        var server = new SeikoMcpServer(fixture.Root, allowMutations: false);

        var result = CallTool(server, "outdated", new JsonObject { ["root"] = fixture.Root });

        var item = JsonNode.Parse(ToolText(result))!.AsArray().Single()!;
        var blocker = item["blockers"]!.AsArray().Single()!;
        Assert.Equal("declaration", blocker["relation"]!.GetValue<string>());
        Assert.Equal("[1.0.0]", blocker["range"]!.GetValue<string>());
    }

    [Fact]
    public void ToolArguments_RejectWrongValueTypes()
    {
        var root = NewDirectory();
        try
        {
            var server = new SeikoMcpServer(root, allowMutations: true);

            var frozen = CallTool(
                server,
                "sync",
                new JsonObject { ["root"] = root, ["frozen"] = "true" });
            Assert.True(frozen["isError"]!.GetValue<bool>());
            Assert.Contains("frozen must be a boolean", ToolText(frozen));

            var jobs = CallTool(
                server,
                "add",
                new JsonObject { ["root"] = root, ["spec"] = "hello", ["jobs"] = "4" });
            Assert.True(jobs["isError"]!.GetValue<bool>());
            Assert.Contains("jobs must be an integer", ToolText(jobs));

            var syncJobs = CallTool(
                server,
                "sync",
                new JsonObject { ["root"] = root, ["jobs"] = "4" });
            Assert.True(syncJobs["isError"]!.GetValue<bool>());
            Assert.Contains("jobs must be an integer", ToolText(syncJobs));

            var syncProxy = CallTool(
                server,
                "sync",
                new JsonObject { ["root"] = root, ["proxy"] = 4 });
            Assert.True(syncProxy["isError"]!.GetValue<bool>());
            Assert.Contains("proxy must be a string", ToolText(syncProxy));

            var readOnly = CallTool(
                server,
                "list",
                new JsonObject { ["root"] = 123 });
            Assert.True(readOnly["isError"]!.GetValue<bool>());
            Assert.Contains("root must be a string", ToolText(readOnly));

            var frozenNull = CallTool(
                server,
                "sync",
                new JsonObject { ["root"] = root, ["frozen"] = (JsonNode?)null });
            Assert.True(frozenNull["isError"]!.GetValue<bool>());
            Assert.Contains("frozen must be a boolean", ToolText(frozenNull));

            var rootNull = CallTool(
                server,
                "list",
                new JsonObject { ["root"] = (JsonNode?)null });
            Assert.True(rootNull["isError"]!.GetValue<bool>());
            Assert.Contains("root must be a string", ToolText(rootNull));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    [Fact]
    public void MutationStore_AllowlistAppliesWithoutRootAllowlist()
    {
        var root = NewDirectory();
        var store = NewDirectory();
        var outside = NewDirectory();
        try
        {
            var server = new SeikoMcpServer(
                root,
                allowMutations: true,
                allowedStores: [store]);

            var result = CallTool(
                server,
                "sync",
                new JsonObject { ["root"] = root, ["store"] = outside });

            Assert.True(result["isError"]!.GetValue<bool>());
            Assert.Contains("outside --allowed-store", ToolText(result));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(store, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void MutationStore_DefaultStoreMustPassTheAllowlist()
    {
        var root = NewDirectory();
        var store = NewDirectory();
        var outside = NewDirectory();
        try
        {
            var server = new SeikoMcpServer(
                root,
                allowMutations: true,
                allowedStores: [store],
                defaultStore: outside);

            var result = CallTool(
                server,
                "sync",
                new JsonObject { ["root"] = root });

            Assert.True(result["isError"]!.GetValue<bool>());
            Assert.Contains("outside --allowed-store", ToolText(result));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
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
