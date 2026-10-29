using System.Text.Json;
using System.Text.Json.Nodes;
using Seiko;

namespace Seiko.McpServer;

public sealed class McpServer
{
    private const string ProtocolVersion = "2025-06-18";
    private readonly string _defaultRoot;
    private readonly bool _allowMutations;
    private readonly SeikoClient _engine = new();

    public McpServer(string defaultRoot, bool allowMutations)
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
            Tool("search", "Search package names in the configured indexes.", SearchSchema()),
            Tool("info", "Show versions, dependencies, and artifact counts.", InfoSchema()),
            Tool("lock", "Return the current lock file.", RootSchema(required: false)),
            Tool("source_list", "List configured sources.", RootSchema(required: false)),
        };

        if (_allowMutations)
        {
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
        var lockFile = _engine.ReadLock(root);
        var sei = new JsonArray();
        if (lockFile is not null)
        {
            foreach (var item in lockFile.Packages)
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
        foreach (var result in _engine.Search(root, term))
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
        var info = _engine.Info(root, name);
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
        var lockFile = _engine.ReadLock(root);
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
        var result = _engine.Sync(root, OptionalString(arguments, "store"), Bool(arguments, "pre"), mode);
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
        var result = _engine.Rollback(root, OptionalString(arguments, "store"), Int(arguments, "to"));
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
