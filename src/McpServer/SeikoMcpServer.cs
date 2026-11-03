using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Seiko;

namespace Seiko.McpServer;

public sealed class SeikoMcpServer
{
    private const string ProtocolVersion = "2025-06-18";

    public static string Version { get; } =
        typeof(SeikoMcpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(SeikoMcpServer).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly string _defaultRoot;
    private readonly bool _allowMutations;
    private readonly IReadOnlyList<string> _allowedRoots;
    private readonly IReadOnlyList<string> _allowedStores;
    private readonly string _defaultStore;
    private readonly SeikoClient _client = new();
    private readonly string? _auditLog;

    public SeikoMcpServer(
        string defaultRoot,
        bool allowMutations,
        IReadOnlyList<string>? allowedRoots = null,
        IReadOnlyList<string>? allowedStores = null,
        string? defaultStore = null,
        string? auditLog = null)
    {
        // A bad audit path must fail at startup, while failures to append at
        // call time must not change the outcome of a completed operation.
        _auditLog = string.IsNullOrEmpty(auditLog) ? null : ValidateAuditPath(auditLog);
        _defaultRoot = Path.GetFullPath(defaultRoot);
        _allowMutations = allowMutations;
        _allowedRoots = (allowedRoots ?? [])
            .Select(SeikoClient.CanonicalizePath)
            .Distinct(PathComparer)
            .ToList();
        _allowedStores = (allowedStores ?? [])
            .Select(SeikoClient.CanonicalizePath)
            .Distinct(PathComparer)
            .ToList();
        _defaultStore = SeikoClient.CanonicalizePath(defaultStore ?? SeikoClient.DefaultStorePath());
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

        var version = request["jsonrpc"];
        if (version is null
            || version.GetValueKind() != JsonValueKind.String
            || !string.Equals(version.GetValue<string>(), "2.0", StringComparison.Ordinal))
        {
            return Error(null, -32600, "invalid jsonrpc version");
        }

        var idNode = request["id"];
        if (idNode is not null
            && idNode.GetValueKind() is not (JsonValueKind.String
                or JsonValueKind.Number
                or JsonValueKind.Null))
        {
            return Error(null, -32600, "invalid id");
        }

        var id = idNode?.DeepClone();
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
        // One supported version: report it whether or not the client asked for
        // something else, as MCP version negotiation requires.
        return new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "seiko-mcp", ["version"] = Version },
        };
    }

    private JsonObject ToolsList()
    {
        var tools = new JsonArray(
            Tool("list", "List installed sei.", RootSchema(required: false)),
            Tool("search", "Search sei names in the configured indexes.", SearchSchema()),
            Tool("info", "Show versions, dependencies, and artifact counts.", InfoSchema()),
            Tool("lock", "Return the current lock file.", RootSchema(required: false)),
            Tool("source_list", "List configured sources.", RootSchema(required: false)),
            Tool("config_get", "Read an install-root configuration value.", ConfigGetSchema()),
            Tool("cache_status", "Show store artifact and partial counts.", StoreSchema()),
            Tool("cache_verify", "Verify stored artifacts.", StoreSchema()),
            Tool("why", "Explain why a sei is in the dependency graph.", WhySchema()),
            Tool("outdated", "Report sei that have newer versions.", OutdatedSchema()),
            Tool("index_verify", "Validate a static index directory.", IndexVerifySchema()),
            Tool("doctor", "Check the lock, store, deployment, and partial downloads.", DoctorSchema()),
            Tool("check", "Read-only doctor without cleaning.", RootSchema(required: true)));

        if (_allowMutations)
        {
            tools.Add((JsonNode)Tool("add", "Add a direct sei declaration and sync.", AddSchema()));
            tools.Add((JsonNode)Tool("remove", "Remove a direct sei declaration and sync.", RemoveSchema()));
            tools.Add((JsonNode)Tool("update", "Update direct declarations and sync.", UpdateSchema()));
            tools.Add((JsonNode)Tool("source_add", "Add a source.", SourceAddSchema()));
            tools.Add((JsonNode)Tool("source_remove", "Remove a source.", SourceRemoveSchema()));
            tools.Add((JsonNode)Tool("config_set", "Write an install-root configuration value.", ConfigSetSchema()));
            tools.Add((JsonNode)Tool("cache_clean", "Remove artifacts not referenced by this root.", CacheCleanSchema()));
            tools.Add((JsonNode)Tool("sync", "Resolve and deploy sei.", SyncSchema()));
            tools.Add((JsonNode)Tool("rollback", "Restore a previous generation.", RollbackSchema()));
            tools.Add((JsonNode)Tool("init", "Write a minimal seiko.conf.", RootSchema(required: true)));
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
                "config_get" => ConfigGet(arguments),
                "cache_status" => CacheStatus(arguments),
                "cache_verify" => CacheVerify(arguments),
                "why" => Why(arguments),
                "outdated" => Outdated(arguments),
                "index_verify" => IndexVerify(arguments),
                "doctor" => Doctor(arguments),
                "check" => Check(arguments),
                "add" => RequireMutations(() => Add(arguments)),
                "remove" => RequireMutations(() => Remove(arguments)),
                "update" => RequireMutations(() => Update(arguments)),
                "source_add" => RequireMutations(() => SourceAdd(arguments)),
                "source_remove" => RequireMutations(() => SourceRemove(arguments)),
                "config_set" => RequireMutations(() => ConfigSet(arguments)),
                "cache_clean" => RequireMutations(() => CacheClean(arguments)),
                "sync" => RequireMutations(() => Sync(arguments)),
                "rollback" => RequireMutations(() => Rollback(arguments)),
                "init" => RequireMutations(() => Init(arguments)),
                _ => throw new SeikoException($"unknown tool '{name}'"),
            };
            Audit("ok", name, arguments, text);
            return ToolResult(text);
        }
        catch (SeikoException ex)
        {
            Audit("error", name, arguments, ex.Message);
            return ToolError(ex.Message);
        }
        catch (Exception ex)
        {
            Audit("error", name, arguments, ex.Message);
            return ToolError(ex.Message);
        }
    }

    // One line per call. Arguments can contain URLs, so both arguments and the
    // result are redacted before they reach the log.
    private void Audit(string outcome, string tool, JsonObject arguments, string text)
    {
        if (_auditLog is null)
        {
            return;
        }

        try
        {
            var entry = new JsonObject
            {
                ["time"] = DateTimeOffset.UtcNow.ToString("O"),
                ["tool"] = tool,
                ["outcome"] = outcome,
                ["arguments"] = RedactUrls(arguments.DeepClone()),
                ["result"] = Urls.RedactText(text.Length > 4096 ? text[..4096] : text),
            };
            File.AppendAllText(_auditLog, entry.ToJsonString() + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An audit failure must not fail the call itself.
        }
    }

    private static string ValidateAuditPath(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new SeikoException($"invalid audit log path '{path}': {ex.Message}", "usage");
        }

        if (Directory.Exists(full))
        {
            throw new SeikoException($"audit log path '{path}' is a directory", "usage");
        }

        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SeikoException($"cannot create audit log directory '{directory}': {ex.Message}", "usage");
            }
        }

        return full;
    }

    // Arguments can carry URLs (proxy, source index) with userinfo or signed
    // query strings; redact them before they reach a persisted log.
    private static JsonNode RedactUrls(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj.ToList())
                {
                    obj[pair.Key] = RedactUrls(pair.Value);
                }

                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    array[i] = RedactUrls(array[i]);
                }

                return array;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(Urls.RedactText(text))!;
            default:
                return node ?? JsonValue.Create((string?)null)!;
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
                sei.Add((JsonNode)new JsonObject
                {
                    ["name"] = item.Name,
                    ["version"] = item.Version,
                    ["source"] = item.Source,
                    ["direct"] = item.Direct,
                    ["selectors"] = RidNode(item.Artifact.Rid),
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
        foreach (var result in _client.Search(
            root,
            term,
            OptionalString(arguments, "proxy"),
            Bool(arguments, "offline"),
            StorePath(arguments)))
        {
            results.Add((JsonNode)new JsonObject
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
        var info = _client.Info(
            root,
            name,
            OptionalString(arguments, "proxy"),
            Bool(arguments, "offline"),
            StorePath(arguments));
        var versions = new JsonArray();
        foreach (var version in info.Versions)
        {
            versions.Add((JsonNode)new JsonObject
            {
                ["version"] = version.Version,
                ["yanked"] = version.Yanked,
                ["deprecated"] = version.Deprecated,
                ["advisory"] = version.Advisory is null
                    ? null
                    : new JsonObject
                    {
                        ["severity"] = version.Advisory.Severity,
                        ["url"] = version.Advisory.Url,
                    },
                ["artifacts"] = version.ArtifactCount,
                ["dependencies"] = DependenciesNode(version.Dependencies),
                ["prerequisites"] = DependenciesNode(version.Prerequisites),
                ["optionalPeers"] = DependenciesNode(version.OptionalPeers),
                ["conflicts"] = DependenciesNode(version.Conflicts),
                ["artifactDetails"] = ArtifactDetailsNode(version.Artifacts),
            });
        }

        return new JsonObject
        {
            ["name"] = info.Name,
            ["source"] = info.Source,
            ["unlisted"] = info.Unlisted,
            ["deprecated"] = info.Deprecated,
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
        => SourcesJson(_client.ListSources(Root(arguments, required: false)));

    private static string SourcesJson(IReadOnlyList<SourceConfig> sources)
    {
        var array = new JsonArray();
        foreach (var source in sources)
        {
            array.Add((JsonNode)SourceJson(source));
        }

        return array.ToJsonString();
    }

    private static JsonObject SourceJson(SourceConfig source)
    {
        var hosts = new JsonArray();
        foreach (var host in source.TokenHosts ?? [])
        {
            hosts.Add((JsonNode)host);
        }

        return new JsonObject
        {
            ["id"] = source.Id,
            ["index"] = source.Index,
            ["tokenHosts"] = hosts,
        };
    }

    private string SourceAdd(JsonObject arguments)
    {
        var root = Root(arguments, required: true);
        var id = RequiredString(arguments, "id");
        var index = RequiredString(arguments, "index");
        var sources = _client.SourceAdd(
            root,
            id,
            index,
            StringArray(arguments, "tokenHosts"));
        return SourcesJson(sources);
    }

    private string SourceRemove(JsonObject arguments)
    {
        var root = Root(arguments, required: true);
        var id = RequiredString(arguments, "id");
        return SourcesJson(_client.SourceRemove(root, id));
    }

    private string ConfigGet(JsonObject arguments)
    {
        var root = Root(arguments, required: false);
        var key = RequiredString(arguments, "key");
        return new JsonObject { ["rid"] = _client.ConfigGet(root, key) }.ToJsonString();
    }

    private string ConfigSet(JsonObject arguments)
    {
        var root = Root(arguments, required: true);
        var key = RequiredString(arguments, "key");
        var value = OptionalString(arguments, "value");
        _client.ConfigSet(root, key, value);
        return new JsonObject { ["rid"] = value }.ToJsonString();
    }

    private string CacheStatus(JsonObject arguments)
    {
        var status = _client.CacheStatus(OptionalString(arguments, "store"));
        return new JsonObject
        {
            ["artifacts"] = status.Artifacts,
            ["bytes"] = status.Bytes,
            ["partialItems"] = status.PartialItems,
            ["stagingItems"] = status.StagingItems,
            ["stagingBytes"] = status.StagingBytes,
        }.ToJsonString();
    }

    private string CacheVerify(JsonObject arguments)
    {
        var findings = new JsonArray();
        foreach (var finding in _client.CacheVerify(OptionalString(arguments, "store")))
        {
            findings.Add((JsonNode)new JsonObject
            {
                ["severity"] = finding.Severity,
                ["code"] = finding.Code,
                ["message"] = finding.Message,
                ["path"] = finding.Path,
            });
        }

        return findings.ToJsonString();
    }

    private string CacheClean(JsonObject arguments)
    {
        var root = Root(arguments, required: true);
        var result = _client.CacheClean(root, StorePath(arguments));
        return new JsonObject
        {
            ["removed"] = result.Removed,
            ["bytes"] = result.Bytes,
        }.ToJsonString();
    }

    private string Why(JsonObject arguments)
    {
        var name = RequiredString(arguments, "name");
        var root = Root(arguments, required: false);
        var chains = new JsonArray();
        foreach (var chain in _client.WhyDetailed(root, name))
        {
            var path = new JsonArray();
            foreach (var link in chain)
            {
                path.Add((JsonNode)new JsonObject
                {
                    ["name"] = link.Name,
                    ["kind"] = link.Kind,
                });
            }

            chains.Add((JsonNode)path);
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
            Bool(arguments, "pre"),
            Bool(arguments, "offline"),
            StorePath(arguments)))
        {
            var blockers = new JsonArray();
            foreach (var blocker in item.Blockers)
            {
                blockers.Add((JsonNode)new JsonObject
                {
                    ["relation"] = blocker.Relation,
                    ["target"] = blocker.Target,
                    ["range"] = blocker.Range,
                    ["source"] = blocker.Source,
                });
            }

            items.Add((JsonNode)new JsonObject
            {
                ["name"] = item.Name,
                ["current"] = item.Current,
                ["latestInRange"] = item.LatestInRange,
                ["latest"] = item.Latest,
                ["latestInstallable"] = item.LatestInstallable,
                ["blockers"] = blockers,
            });
        }

        return items.ToJsonString();
    }

    private string Add(JsonObject arguments)
    {
        var specs = StringArray(arguments, "specs");
        if (specs is null || specs.Count == 0)
        {
            var spec = OptionalString(arguments, "spec");
            if (string.IsNullOrWhiteSpace(spec))
            {
                throw new SeikoException("add requires specs or spec", "usage");
            }

            specs = [spec];
        }

        var root = Root(arguments, required: true);
        var dryRun = Bool(arguments, "dryRun");
        var result = _client.AddMany(
            root,
            specs,
            StringArray(arguments, "remove"),
            StorePath(arguments),
            Bool(arguments, "pre"),
            Int(arguments, "jobs") ?? DownloadSession.DefaultJobs,
            OptionalString(arguments, "proxy"),
            latestInstallable: Bool(arguments, "latestInstallable"),
            allowCritical: Bool(arguments, "allowCritical"),
            dryRun: dryRun,
            adopt: Bool(arguments, "adopt"),
            force: Bool(arguments, "force"),
            offline: Bool(arguments, "offline"));
        return MutationResult(result, dryRun);
    }

    private string Remove(JsonObject arguments)
    {
        var name = RequiredString(arguments, "name");
        var root = Root(arguments, required: true);
        var dryRun = Bool(arguments, "dryRun");
        var result = _client.Remove(
            root,
            name,
            StorePath(arguments),
            Bool(arguments, "pre"),
            Int(arguments, "jobs") ?? DownloadSession.DefaultJobs,
            OptionalString(arguments, "proxy"),
            allowCritical: Bool(arguments, "allowCritical"),
            dryRun: dryRun,
            purge: Bool(arguments, "purge"),
            adopt: Bool(arguments, "adopt"),
            force: Bool(arguments, "force"),
            offline: Bool(arguments, "offline"));
        return MutationResult(result, dryRun);
    }

    private string Update(JsonObject arguments)
    {
        var root = Root(arguments, required: true);
        var dryRun = Bool(arguments, "dryRun");
        var result = _client.Update(
            root,
            OptionalString(arguments, "name"),
            Bool(arguments, "latest"),
            StorePath(arguments),
            Bool(arguments, "pre"),
            Int(arguments, "jobs") ?? DownloadSession.DefaultJobs,
            OptionalString(arguments, "proxy"),
            latestInstallable: Bool(arguments, "latestInstallable"),
            allowCritical: Bool(arguments, "allowCritical"),
            dryRun: dryRun,
            adopt: Bool(arguments, "adopt"),
            force: Bool(arguments, "force"),
            offline: Bool(arguments, "offline"));
        return MutationResult(result, dryRun);
    }

    private static string MutationResult(SyncResult result, bool dryRun)
        => dryRun ? PreviewJson(result.Preview!) : SyncResultJson(result);

    private static string PreviewJson(PreviewResult preview)
    {
        var changes = new JsonArray();
        foreach (var change in preview.Changes)
        {
            changes.Add((JsonNode)new JsonObject
            {
                ["action"] = change.Action,
                ["name"] = change.Name,
                ["from"] = change.From,
                ["to"] = change.To,
                ["source"] = change.Source,
                ["artifact"] = change.Artifact,
            });
        }

        var blocked = new JsonArray();
        foreach (var block in preview.Blocked)
        {
            blocked.Add((JsonNode)new JsonObject
            {
                ["code"] = block.Code,
                ["message"] = block.Message,
            });
        }

        var diagnostics = new JsonArray();
        foreach (var diagnostic in preview.Diagnostics)
        {
            diagnostics.Add((JsonNode)new JsonObject
            {
                ["name"] = diagnostic.Name,
                ["kind"] = diagnostic.Kind,
                ["message"] = diagnostic.Message,
            });
        }

        return new JsonObject
        {
            ["changes"] = changes,
            ["blocked"] = blocked,
            ["diagnostics"] = diagnostics,
            ["downloaded"] = false,
        }.ToJsonString();
    }

    private static JsonArray DiagnosticsNode(IReadOnlyList<PreviewDiagnostic> diagnostics)
    {
        var array = new JsonArray();
        foreach (var diagnostic in diagnostics)
        {
            array.Add((JsonNode)new JsonObject
            {
                ["name"] = diagnostic.Name,
                ["kind"] = diagnostic.Kind,
                ["message"] = diagnostic.Message,
            });
        }

        return array;
    }

    private static JsonArray WarningsNode(SyncResult result)
    {
        var warnings = new JsonArray();
        foreach (var warning in result.Warnings)
        {
            warnings.Add((JsonNode)JsonValue.Create(warning));
        }

        return warnings;
    }

    private static string SyncResultJson(SyncResult result)
        => new JsonObject
        {
            ["sei"] = result.SeiInstalled,
            ["files"] = result.FilesDeployed,
            ["mode"] = SyncMode.Normal.ToString(),
            ["warnings"] = WarningsNode(result),
        }.ToJsonString();

    private string Sync(JsonObject arguments)
    {
        var root = Root(arguments, required: true);
        var diagnostics = Bool(arguments, "diagnostics");
        var frozen = Bool(arguments, "frozen");
        var locked = Bool(arguments, "locked");
        if (frozen && locked)
        {
            throw new SeikoException("frozen and locked are mutually exclusive");
        }

        var mode = frozen ? SyncMode.Frozen : locked ? SyncMode.Locked : SyncMode.Normal;
        var dryRun = Bool(arguments, "dryRun");
        var result = _client.Sync(
            root,
            StorePath(arguments),
            Bool(arguments, "pre"),
            mode,
            jobs: Int(arguments, "jobs") ?? DownloadSession.DefaultJobs,
            proxy: OptionalString(arguments, "proxy"),
            latestInstallable: Bool(arguments, "latestInstallable"),
            allowCritical: Bool(arguments, "allowCritical"),
            dryRun: dryRun,
            adopt: Bool(arguments, "adopt"),
            force: Bool(arguments, "force"),
            offline: Bool(arguments, "offline"));
        if (dryRun)
        {
            return MutationResult(result, dryRun: true);
        }

        return new JsonObject
        {
            ["sei"] = result.SeiInstalled,
            ["files"] = result.FilesDeployed,
            ["mode"] = mode.ToString(),
            ["warnings"] = WarningsNode(result),
            ["diagnostics"] = diagnostics ? DiagnosticsNode(result.Diagnostics) : null,
        }.ToJsonString();
    }

    private string IndexVerify(JsonObject arguments)
    {
        var path = OptionalString(arguments, "path");
        var strict = Bool(arguments, "strict");
        var result = IndexVerifier.Verify(
            path ?? Directory.GetCurrentDirectory(),
            Bool(arguments, "checkArtifacts"),
            OptionalString(arguments, "proxy"));
        var findings = new JsonArray();
        foreach (var finding in result.Findings)
        {
            findings.Add((JsonNode)new JsonObject
            {
                ["severity"] = finding.Severity,
                ["code"] = finding.Code,
                ["message"] = finding.Message,
                ["path"] = finding.Path,
            });
        }

        return new JsonObject
        {
            ["root"] = result.Root,
            ["sei"] = result.Sei,
            ["versions"] = result.Versions,
            ["artifacts"] = result.Artifacts,
            ["checkedArtifacts"] = result.CheckedArtifacts,
            ["findings"] = findings,
            ["errors"] = result.Errors,
            ["warnings"] = result.Warnings,
            ["failed"] = result.Errors > 0 || (strict && result.Warnings > 0),
        }.ToJsonString();
    }

    private string Init(JsonObject arguments)
    {
        var root = Root(arguments, required: true);
        var path = InitTemplate.Write(root);
        return new JsonObject { ["created"] = path }.ToJsonString();
    }

    private string Check(JsonObject arguments) => RunDoctor(arguments, clean: false);

    // clean=true deletes .part files, so it needs the mutation gate; plain
    // diagnostics stay available without it.
    private string Doctor(JsonObject arguments)
        => Bool(arguments, "clean")
            ? RequireMutations(() => RunDoctor(arguments, clean: true))
            : RunDoctor(arguments, clean: false);

    private string RunDoctor(JsonObject arguments, bool clean)
    {
        var root = Root(arguments, required: true);
        var result = new Doctor().Run(root, StorePath(arguments), clean);
        var findings = new JsonArray();
        foreach (var finding in result.Findings)
        {
            findings.Add((JsonNode)new JsonObject
            {
                ["severity"] = finding.Severity,
                ["code"] = finding.Code,
                ["message"] = finding.Message,
                ["path"] = finding.Path,
            });
        }

        return new JsonObject
        {
            ["findings"] = findings,
            ["cleanCount"] = result.CleanCount,
        }.ToJsonString();
    }

    private string Rollback(JsonObject arguments)
    {
        var root = Root(arguments, required: true);
        var result = _client.Rollback(
            root,
            StorePath(arguments),
            Int(arguments, "to"),
            OptionalString(arguments, "proxy"),
            Bool(arguments, "offline"));
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
            var full = SeikoClient.CanonicalizePath(root);
            if (required && _allowedRoots.Count > 0 && !RootedPath.IsUnderAny(full, _allowedRoots))
            {
                throw new SeikoException($"root '{full}' is outside --allowed-root", "usage");
            }

            return full;
        }

        if (required)
        {
            throw new SeikoException("root is required for mutation tools", "usage");
        }

        return _defaultRoot;
    }

    private string StorePath(JsonObject arguments)
    {
        var store = OptionalString(arguments, "store");
        var full = SeikoClient.CanonicalizePath(store ?? _defaultStore);
        if (_allowedRoots.Count == 0 && _allowedStores.Count == 0)
        {
            return full;
        }

        if (_allowedStores.Count > 0)
        {
            if (!RootedPath.IsUnderAny(full, _allowedStores))
            {
                throw new SeikoException($"store '{full}' is outside --allowed-store", "usage");
            }

            return full;
        }

        if (!RootedPath.IsUnderAny(full, new[] { _defaultStore }))
        {
            throw new SeikoException($"store '{full}' is outside the default store", "usage");
        }

        return full;
    }

    private static string RequiredString(JsonObject arguments, string name)
        => OptionalString(arguments, name) ?? throw new SeikoException($"{name} is required");

    private static string? OptionalString(JsonObject arguments, string name)
    {
        if (!arguments.TryGetPropertyValue(name, out var node))
        {
            return null;
        }

        if (node is null)
        {
            throw new SeikoException($"{name} must be a string");
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        throw new SeikoException($"{name} must be a string");
    }

    private static bool Bool(JsonObject arguments, string name)
    {
        if (!arguments.TryGetPropertyValue(name, out var node))
        {
            return false;
        }

        if (node is null)
        {
            throw new SeikoException($"{name} must be a boolean");
        }

        if (node is JsonValue value && value.TryGetValue<bool>(out var result))
        {
            return result;
        }

        throw new SeikoException($"{name} must be a boolean");
    }

    private static int? Int(JsonObject arguments, string name)
    {
        if (!arguments.TryGetPropertyValue(name, out var node))
        {
            return null;
        }

        if (node is null)
        {
            throw new SeikoException($"{name} must be an integer");
        }

        if (node is JsonValue value && value.TryGetValue<int>(out var result))
        {
            return result;
        }

        throw new SeikoException($"{name} must be an integer");
    }

    private static IReadOnlyList<string>? StringArray(JsonObject arguments, string name)
    {
        if (!arguments.TryGetPropertyValue(name, out var node))
        {
            return null;
        }

        if (node is not JsonArray array)
        {
            throw new SeikoException($"{name} must be an array of strings");
        }

        var result = new List<string>();
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var text))
            {
                throw new SeikoException($"{name} must be an array of strings");
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                result.Add(text.Trim());
            }
        }

        return result;
    }

    private static JsonObject? RidNode(string? rid)
    {
        if (rid is null)
        {
            return null;
        }

        var node = new JsonObject
        {
            ["rid"] = rid,
        };

        return node;
    }

    private static JsonArray DependenciesNode(IReadOnlyList<SeiDependency> dependencies)
    {
        var array = new JsonArray();
        foreach (var dependency in dependencies)
        {
            array.Add((JsonNode)new JsonObject
            {
                ["name"] = dependency.Name,
                ["version"] = dependency.Version,
            });
        }

        return array;
    }

    private static JsonArray ArtifactDetailsNode(IReadOnlyList<SeiArtifactInfo> artifacts)
    {
        var array = new JsonArray();
        foreach (var artifact in artifacts)
        {
            array.Add((JsonNode)new JsonObject
            {
                ["rid"] = artifact.Rid,
                ["dependencies"] = DependenciesNode(artifact.Dependencies),
                ["prerequisites"] = DependenciesNode(artifact.Prerequisites),
                ["optionalPeers"] = DependenciesNode(artifact.OptionalPeers),
                ["conflicts"] = DependenciesNode(artifact.Conflicts),
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
                ["proxy"] = new JsonObject { ["type"] = "string" },
                ["store"] = new JsonObject { ["type"] = "string" },
                ["offline"] = new JsonObject { ["type"] = "boolean" },
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
                ["proxy"] = new JsonObject { ["type"] = "string" },
                ["store"] = new JsonObject { ["type"] = "string" },
                ["offline"] = new JsonObject { ["type"] = "boolean" },
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
                ["latestInstallable"] = new JsonObject { ["type"] = "boolean" },
                ["allowCritical"] = new JsonObject { ["type"] = "boolean" },
                ["dryRun"] = new JsonObject { ["type"] = "boolean" },
                ["adopt"] = new JsonObject { ["type"] = "boolean" },
                ["force"] = new JsonObject { ["type"] = "boolean" },
                ["offline"] = new JsonObject { ["type"] = "boolean" },
                ["diagnostics"] = new JsonObject { ["type"] = "boolean" },
                ["jobs"] = new JsonObject { ["type"] = "integer" },
                ["proxy"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray(JsonValue.Create("root")),
        };

    private static JsonObject ConfigGetSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["root"] = new JsonObject { ["type"] = "string" },
                ["key"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray(JsonValue.Create("key")),
        };

    private static JsonObject StoreSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["store"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray(),
        };

    private static JsonObject SourceAddSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["root"] = new JsonObject { ["type"] = "string" },
                ["id"] = new JsonObject { ["type"] = "string" },
                ["index"] = new JsonObject { ["type"] = "string" },
                ["tokenHosts"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                },
            },
            ["required"] = new JsonArray(
                JsonValue.Create("root"),
                JsonValue.Create("id"),
                JsonValue.Create("index")),
        };

    private static JsonObject SourceRemoveSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["root"] = new JsonObject { ["type"] = "string" },
                ["id"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray(
                JsonValue.Create("root"),
                JsonValue.Create("id")),
        };

    private static JsonObject ConfigSetSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["root"] = new JsonObject { ["type"] = "string" },
                ["key"] = new JsonObject { ["type"] = "string" },
                ["value"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray(
                JsonValue.Create("root"),
                JsonValue.Create("key")),
        };

    private static JsonObject CacheCleanSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["root"] = new JsonObject { ["type"] = "string" },
                ["store"] = new JsonObject { ["type"] = "string" },
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
                ["store"] = new JsonObject { ["type"] = "string" },
                ["offline"] = new JsonObject { ["type"] = "boolean" },
            },
            ["required"] = new JsonArray(),
        };

    private static JsonObject AddSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["root"] = new JsonObject { ["type"] = "string" },
                ["specs"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                },
                ["remove"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                },
                ["spec"] = new JsonObject { ["type"] = "string" },
                ["store"] = new JsonObject { ["type"] = "string" },
                ["pre"] = new JsonObject { ["type"] = "boolean" },
                ["jobs"] = new JsonObject { ["type"] = "integer" },
                ["proxy"] = new JsonObject { ["type"] = "string" },
                ["latestInstallable"] = new JsonObject { ["type"] = "boolean" },
                ["allowCritical"] = new JsonObject { ["type"] = "boolean" },
                ["dryRun"] = new JsonObject { ["type"] = "boolean" },
                ["adopt"] = new JsonObject { ["type"] = "boolean" },
                ["force"] = new JsonObject { ["type"] = "boolean" },
                ["offline"] = new JsonObject { ["type"] = "boolean" },
            },
            ["required"] = new JsonArray(JsonValue.Create("root")),
        };

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
                ["latestInstallable"] = new JsonObject { ["type"] = "boolean" },
                ["allowCritical"] = new JsonObject { ["type"] = "boolean" },
                ["dryRun"] = new JsonObject { ["type"] = "boolean" },
                ["adopt"] = new JsonObject { ["type"] = "boolean" },
                ["force"] = new JsonObject { ["type"] = "boolean" },
                ["offline"] = new JsonObject { ["type"] = "boolean" },
                ["jobs"] = new JsonObject { ["type"] = "integer" },
                ["proxy"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray(JsonValue.Create("root")),
        };

    private static JsonObject SyncMutationSchema(bool requireSpec)
    {
        var properties = new JsonObject
        {
            ["root"] = new JsonObject { ["type"] = "string" },
            [requireSpec ? "spec" : "name"] = new JsonObject { ["type"] = "string" },
            ["store"] = new JsonObject { ["type"] = "string" },
            ["pre"] = new JsonObject { ["type"] = "boolean" },
            ["jobs"] = new JsonObject { ["type"] = "integer" },
            ["proxy"] = new JsonObject { ["type"] = "string" },
        };
        if (requireSpec)
        {
            properties["latestInstallable"] = new JsonObject { ["type"] = "boolean" };
        }

        properties["allowCritical"] = new JsonObject { ["type"] = "boolean" };
        properties["dryRun"] = new JsonObject { ["type"] = "boolean" };
        properties["adopt"] = new JsonObject { ["type"] = "boolean" };
        properties["force"] = new JsonObject { ["type"] = "boolean" };
        properties["offline"] = new JsonObject { ["type"] = "boolean" };
        if (!requireSpec)
        {
            properties["purge"] = new JsonObject { ["type"] = "boolean" };
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(
                JsonValue.Create("root"),
                JsonValue.Create(requireSpec ? "spec" : "name")),
        };
    }

    private static JsonObject DoctorSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["root"] = new JsonObject { ["type"] = "string" },
                ["store"] = new JsonObject { ["type"] = "string" },
                ["clean"] = new JsonObject { ["type"] = "boolean" },
            },
            ["required"] = new JsonArray(JsonValue.Create("root")),
        };

    private static JsonObject IndexVerifySchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "string" },
                ["checkArtifacts"] = new JsonObject { ["type"] = "boolean" },
                ["strict"] = new JsonObject { ["type"] = "boolean" },
                ["proxy"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray(),
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
                ["proxy"] = new JsonObject { ["type"] = "string" },
                ["offline"] = new JsonObject { ["type"] = "boolean" },
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
