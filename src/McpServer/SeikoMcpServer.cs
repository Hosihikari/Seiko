using System.Text.Json;
using System.Text.Json.Nodes;
using Seiko;

namespace Seiko.McpServer;

public sealed class SeikoMcpServer
{
    private const string ProtocolVersion = "2025-06-18";
    private readonly string _defaultRoot;
    private readonly bool _allowMutations;
    private readonly SeikoClient _client = new();

    public SeikoMcpServer(string defaultRoot, bool allowMutations)
    {
        _defaultRoot = Path.GetFullPath(defaultRoot);
        _allowMutations = allowMutations;
    }

    public string? Handle(string line)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return Error(null, -32700, "parse error");
        }

        if (parsed is not JsonObject request)
        {
            return Error(null, -32600, "invalid request");
        }

        var id = request["id"]?.DeepClone();
        string? method;
        try
        {
            method = request["method"]?.GetValue<string>();
        }
        catch (Exception)
        {
            return id is null ? null : Error(id, -32600, "invalid method");
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            return id is null ? null : Error(id, -32600, "missing method");
        }

        try
        {
            return method switch
            {
                "initialize" => id is null ? null : Result(id, Initialize(request["params"] as JsonObject)),
                "notifications/initialized" => null,
                "ping" => id is null ? null : Result(id, new JsonObject()),
                "tools/list" => id is null ? null : Result(id, ToolsList()),
                "tools/call" => id is null ? null : Result(id, ToolsCall(request["params"] as JsonObject)),
                _ => id is null ? null : Error(id, -32601, $"unknown method '{method}'"),
            };
        }
        catch (SeikoException ex)
        {
            return id is null ? null : Error(id, -32000, ex.Message);
        }
        catch (Exception ex)
        {
            return id is null ? null : Error(id, -32603, ex.Message);
        }
    }

    private static JsonObject Initialize(JsonObject? parameters)
    {
        var requested = parameters?["protocolVersion"]?.GetValue<string>();
        return new JsonObject
        {
            ["protocolVersion"] = string.IsNullOrWhiteSpace(requested) ? ProtocolVersion : requested,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "seiko", ["version"] = "0.1.0" },
        };
    }

    private JsonObject ToolsList()
    {
        var tools = new JsonArray
        {
            Tool("list", "List installed sei.", RootSchema(required: false)),
            Tool("search", "Search sei names in the configured indexes.", SearchSchema()),
            Tool("info", "Show versions, dependencies, and artifact counts.", InfoSchema()),
            Tool("lock", "Return the current lock file.", RootSchema(required: false)),
            Tool("source_list", "List configured sources.", RootSchema(required: false)),
            Tool("why", "Explain why a sei is in the dependency graph.", WhySchema()),
            Tool("outdated", "Report sei that have newer versions.", OutdatedSchema()),
        };

        if (_allowMutations)
        {
            tools.Add(Tool("add", "Add a direct sei declaration and sync.", AddSchema()));
            tools.Add(Tool("remove", "Remove a direct sei declaration and sync.", RemoveSchema()));
            tools.Add(Tool("update", "Update direct declarations and sync.", UpdateSchema()));
            tools.Add(Tool("sync", "Resolve and deploy sei.", SyncSchema()));
            tools.Add(Tool("rollback", "Restore a previous generation.", RollbackSchema()));
        }

        return new JsonObject { ["tools"] = tools };
    }

    private JsonObject ToolsCall(JsonObject? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            return ToolError("missing tool name");
        }

        var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();
        try
        {
            var text = name switch
            {
                "list" => List(arguments),
                "search" => Search(arguments),
                "info" => Info(arguments),
                "lock" => Lock(arguments),
                "source_list" => SourceList(arguments),
                "why" => Why(arguments),
                "outdated" => Outdated(arguments),
                "add" => RequireMutations(() => Add(arguments)),
                "remove" => RequireMutations(() => Remove(arguments)),
                "update" => RequireMutations(() => Update(arguments)),
                "sync" => RequireMutations(() => Sync(arguments)),
                "rollback" => RequireMutations(() => Rollback(arguments)),
                _ => throw new SeikoException($"unknown tool '{name}'"),
            };
            return ToolResult(text);
        }
        catch (SeikoException ex)
        {
            return ToolError(ex.Message);
        }
        catch (Exception ex)
        {
            return ToolError(ex.Message);
        }
    }

    private string List(JsonObject arguments)
    {
        var root = Root(arguments, required: false);
        var lockFile = _client.ReadLock(root);
        var sei = new JsonArray();
        if (lockFile is not null)
        {
            foreach (var item in lockFile.Sei)
            {
                sei.Add(new JsonObject
                {
                    ["name"] = item.Name,
                    ["version"] = item.Version,
                    ["source"] = item.Source,
                    ["direct"] = item.Direct,
                    ["selectors"] = SelectorsNode(item.Artifact.Selectors),
                });
            }
        }

        return sei.ToJsonString();
    }

    private string Search(JsonObject arguments)
    {
        var term = RequiredString(arguments, "term");
        var root = Root(arguments, required: false);
        var results = new JsonArray();
        foreach (var result in _client.Search(root, term))
        {
            results.Add(new JsonObject
            {
                ["name"] = result.Name,
                ["version"] = result.Version,
                ["source"] = result.Source,
            });
        }

        return results.ToJsonString();
    }

    private string Info(JsonObject arguments)
    {
        var name = RequiredString(arguments, "name");
        var root = Root(arguments, required: false);
        var info = _client.Info(root, name);
        var versions = new JsonArray();
        foreach (var version in info.Versions)
        {
            versions.Add(new JsonObject
            {
                ["version"] = version.Version,
                ["yanked"] = version.Yanked,
                ["deprecated"] = version.Deprecated,
                ["artifacts"] = version.ArtifactCount,
                ["dependencies"] = DependenciesNode(version.Dependencies),
            });
        }

        return new JsonObject
        {
            ["name"] = info.Name,
            ["source"] = info.Source,
            ["versions"] = versions,
        }.ToJsonString();
    }

    private string Lock(JsonObject arguments)
    {
        var root = Root(arguments, required: false);
        var lockFile = _client.ReadLock(root);
        return lockFile is null
            ? "null"
            : JsonSerializer.Serialize(lockFile, SeikoJsonContext.Default.SeikoLock);
    }

    private string SourceList(JsonObject arguments)
    {
        var root = Root(arguments, required: false);
        var config = ConfigLoader.Load(root);
        var sources = new JsonArray();
        foreach (var source in config.Sources)
        {
            sources.Add(new JsonObject
            {
                ["id"] = source.Id,
                ["index"] = source.Index,
                ["priority"] = source.Priority,
            });
        }

        return sources.ToJsonString();
    }

    private string Why(JsonObject arguments)
    {
        var name = RequiredString(arguments, "name");
        var root = Root(arguments, required: false);
        var chains = new JsonArray();
        foreach (var chain in _client.Why(root, name))
        {
            var path = new JsonArray();
            foreach (var part in chain)
            {
                path.Add(part);
            }

            chains.Add(path);
        }

        return chains.ToJsonString();
    }

    private string Outdated(JsonObject arguments)
    {
        var root = Root(arguments, required: false);
        var items = new JsonArray();
        foreach (var item in _client.Outdated(
            root,
            OptionalString(arguments, "proxy"),
            Bool(arguments, "pre")))
        {
            items.Add(new JsonObject
            {
                ["name"] = item.Name,
                ["current"] = item.Current,
                ["latestInRange"] = item.LatestInRange,
                ["latest"] = item.Latest,
            });
        }

        return items.ToJsonString();
    }

    private string Add(JsonObject arguments)
    {
        var spec = RequiredString(arguments, "spec");
        var root = Root(arguments, required: true);
        var result = _client.Add(
            root,
            spec,
            OptionalString(arguments, "store"),
            Bool(arguments, "pre"),
            Int(arguments, "jobs") ?? DownloadSession.DefaultJobs,
            OptionalString(arguments, "proxy"));
        return SyncResultJson(result);
    }

    private string Remove(JsonObject arguments)
    {
        var name = RequiredString(arguments, "name");
        var root = Root(arguments, required: true);
        var result = _client.Remove(
            root,
            name,
            OptionalString(arguments, "store"),
            Bool(arguments, "pre"),
            Int(arguments, "jobs") ?? DownloadSession.DefaultJobs,
            OptionalString(arguments, "proxy"));
        return SyncResultJson(result);
    }

    private string Update(JsonObject arguments)
    {
        var root = Root(arguments, required: true);
        var result = _client.Update(
            root,
            OptionalString(arguments, "name"),
            Bool(arguments, "latest"),
            OptionalString(arguments, "store"),
            Bool(arguments, "pre"),
            Int(arguments, "jobs") ?? DownloadSession.DefaultJobs,
            OptionalString(arguments, "proxy"));
        return SyncResultJson(result);
    }

    private static string SyncResultJson(SyncResult result)
        => new JsonObject
        {
            ["sei"] = result.SeiInstalled,
            ["files"] = result.FilesDeployed,
            ["mode"] = SyncMode.Normal.ToString(),
        }.ToJsonString();

    private string Sync(JsonObject arguments)
    {
        var root = Root(arguments, required: true);
        var frozen = Bool(arguments, "frozen");
        var locked = Bool(arguments, "locked");
        if (frozen && locked)
        {
            throw new SeikoException("frozen and locked are mutually exclusive");
        }

        var mode = frozen ? SyncMode.Frozen : locked ? SyncMode.Locked : SyncMode.Normal;
        var result = _client.Sync(root, OptionalString(arguments, "store"), Bool(arguments, "pre"), mode);
        return new JsonObject
        {
            ["sei"] = result.SeiInstalled,
            ["files"] = result.FilesDeployed,
            ["mode"] = mode.ToString(),
        }.ToJsonString();
    }

    private string Rollback(JsonObject arguments)
    {
        var root = Root(arguments, required: true);
        var result = _client.Rollback(root, OptionalString(arguments, "store"), Int(arguments, "to"));
        return new JsonObject
        {
            ["generation"] = result.Generation,
            ["sei"] = result.SeiInstalled,
            ["files"] = result.FilesDeployed,
        }.ToJsonString();
    }

    private string RequireMutations(Func<string> action)
        => _allowMutations
            ? action()
            : throw new SeikoException("mutations are disabled; start seiko-mcp with --allow-mutations");

    private string Root(JsonObject arguments, bool required)
    {
        var root = OptionalString(arguments, "root");
        if (!string.IsNullOrWhiteSpace(root))
        {
            return Path.GetFullPath(root);
        }

        if (required)
        {
            throw new SeikoException("root is required for mutation tools");
        }

        return _defaultRoot;
    }

    private static string RequiredString(JsonObject arguments, string name)
        => OptionalString(arguments, name) ?? throw new SeikoException($"{name} is required");

    private static string? OptionalString(JsonObject arguments, string name)
        => arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static bool Bool(JsonObject arguments, string name)
        => arguments[name] is JsonValue value && value.TryGetValue<bool>(out var result) && result;

    private static int? Int(JsonObject arguments, string name)
        => arguments[name] is JsonValue value && value.TryGetValue<int>(out var result) ? result : null;

    private static JsonObject? SelectorsNode(Dictionary<string, string>? selectors)
    {
        if (selectors is null || selectors.Count == 0)
        {
            return null;
        }

        var node = new JsonObject();
        foreach (var (key, value) in selectors.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            node[key] = value;
        }

        return node;
    }

    private static JsonArray DependenciesNode(IReadOnlyList<SeiDependency> dependencies)
    {
        var array = new JsonArray();
        foreach (var dependency in dependencies)
        {
            array.Add(new JsonObject
            {
                ["name"] = dependency.Name,
                ["version"] = dependency.Version,
            });
        }

        return array;
    }

    private static JsonObject Tool(string name, string description, JsonObject schema)
        => new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
        };

    private static JsonObject RootSchema(bool required)
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["root"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = required ? new JsonArray(JsonValue.Create("root")) : new JsonArray(),
        };

    private static JsonObject SearchSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["term"] = new JsonObject { ["type"] = "string" },
                ["root"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray(JsonValue.Create("term")),
        };

    private static JsonObject InfoSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject { ["type"] = "string" },
                ["root"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray(JsonValue.Create("name")),
        };

    private static JsonObject SyncSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["root"] = new JsonObject { ["type"] = "string" },
                ["store"] = new JsonObject { ["type"] = "string" },
                ["frozen"] = new JsonObject { ["type"] = "boolean" },
                ["locked"] = new JsonObject { ["type"] = "boolean" },
                ["pre"] = new JsonObject { ["type"] = "boolean" },
            },
            ["required"] = new JsonArray(JsonValue.Create("root")),
        };

    private static JsonObject WhySchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject { ["type"] = "string" },
                ["root"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray(JsonValue.Create("name")),
        };

    private static JsonObject OutdatedSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["root"] = new JsonObject { ["type"] = "string" },
                ["proxy"] = new JsonObject { ["type"] = "string" },
                ["pre"] = new JsonObject { ["type"] = "boolean" },
            },
            ["required"] = new JsonArray(),
        };

    private static JsonObject AddSchema()
        => SyncMutationSchema(requireSpec: true);

    private static JsonObject RemoveSchema()
        => SyncMutationSchema(requireSpec: false);

    private static JsonObject UpdateSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["root"] = new JsonObject { ["type"] = "string" },
                ["name"] = new JsonObject { ["type"] = "string" },
                ["latest"] = new JsonObject { ["type"] = "boolean" },
                ["store"] = new JsonObject { ["type"] = "string" },
                ["pre"] = new JsonObject { ["type"] = "boolean" },
                ["jobs"] = new JsonObject { ["type"] = "integer" },
                ["proxy"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray(JsonValue.Create("root")),
        };

    private static JsonObject SyncMutationSchema(bool requireSpec)
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["root"] = new JsonObject { ["type"] = "string" },
                [requireSpec ? "spec" : "name"] = new JsonObject { ["type"] = "string" },
                ["store"] = new JsonObject { ["type"] = "string" },
                ["pre"] = new JsonObject { ["type"] = "boolean" },
                ["jobs"] = new JsonObject { ["type"] = "integer" },
                ["proxy"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray(
                JsonValue.Create("root"),
                JsonValue.Create(requireSpec ? "spec" : "name")),
        };

    private static JsonObject RollbackSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["root"] = new JsonObject { ["type"] = "string" },
                ["store"] = new JsonObject { ["type"] = "string" },
                ["to"] = new JsonObject { ["type"] = "integer" },
            },
            ["required"] = new JsonArray(JsonValue.Create("root")),
        };

    private static JsonObject ToolResult(string text)
        => new()
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
            }),
            ["isError"] = false,
        };

    private static JsonObject ToolError(string message)
        => new()
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = message,
            }),
            ["isError"] = true,
        };

    private static string Result(JsonNode? id, JsonNode result)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        }.ToJsonString();

    private static string Error(JsonNode? id, int code, string message)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        }.ToJsonString();
}
