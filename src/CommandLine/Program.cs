using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Seiko;
using Seiko.CommandLine;

var rootCommand = new RootCommand("sk - Seiko general-purpose package manager (sk is short for Seiko)");
var jsonOption = new Option<bool>("--json")
{
    Description = "Emit machine-readable JSON.",
    Recursive = true,
};
var verboseOption = new Option<bool>("--verbose", "-v")
{
    Description = "Show debug logs and full stack traces.",
    Recursive = true,
};
var quietOption = new Option<bool>("--quiet", "-q")
{
    Description = "Suppress progress and non-error output.",
    Recursive = true,
};
rootCommand.Add(jsonOption);
rootCommand.Add(verboseOption);
rootCommand.Add(quietOption);

var init = new Command("init", "Write a seiko.conf template.");
var initRoot = RootOption();
init.Add(initRoot);
init.SetAction(parseResult => Run(
    "init",
    IsJson(parseResult),
    () => Init(parseResult.GetValue(initRoot), IsJson(parseResult))));

var sync = new Command("sync", "Resolve sei and deploy files into the install root.");
var syncRoot = RootOption();
var syncStore = StoreOption();
var syncPre = new Option<bool>("--pre") { Description = "Allow prerelease versions." };
var syncFrozen = new Option<bool>("--frozen") { Description = "Replay seiko.lock.json without resolving." };
var syncLocked = new Option<bool>("--locked") { Description = "Fail if the lock is out of date." };
var syncJobs = new Option<int?>("--jobs") { Description = "Maximum concurrent HTTP requests (default 4)." };
var syncProxy = ProxyOption();
sync.Add(syncRoot);
sync.Add(syncStore);
sync.Add(syncPre);
sync.Add(syncFrozen);
sync.Add(syncLocked);
sync.Add(syncJobs);
sync.Add(syncProxy);
sync.SetAction(parseResult => Run(
    "sync",
    IsJson(parseResult),
    () => Sync(
        parseResult.GetValue(syncRoot),
        parseResult.GetValue(syncStore),
        parseResult.GetValue(syncPre),
        parseResult.GetValue(syncFrozen),
        parseResult.GetValue(syncLocked),
        parseResult.GetValue(syncJobs) ?? DownloadSession.DefaultJobs,
        parseResult.GetValue(syncProxy),
        IsJson(parseResult))));
sync.Aliases.Add("install");

var add = new Command("add", "Add a direct sei declaration and sync.");
var addRoot = RootOption();
var addStore = StoreOption();
var addPre = new Option<bool>("--pre") { Description = "Allow prerelease versions." };
var addJobs = JobsOption();
var addProxy = ProxyOption();
var addSpec = new Argument<string>("spec") { Description = "Sei name with an optional @range." };
add.Add(addSpec);
add.Add(addRoot);
add.Add(addStore);
add.Add(addPre);
add.Add(addJobs);
add.Add(addProxy);
add.SetAction(parseResult => Run(
    "add",
    IsJson(parseResult),
    () => Add(
        parseResult.GetValue(addRoot),
        parseResult.GetValue(addSpec)!,
        parseResult.GetValue(addStore),
        parseResult.GetValue(addPre),
        parseResult.GetValue(addJobs) ?? DownloadSession.DefaultJobs,
        parseResult.GetValue(addProxy),
        IsJson(parseResult))));

var remove = new Command("remove", "Remove a direct sei declaration and sync.");
var removeRoot = RootOption();
var removeStore = StoreOption();
var removePre = new Option<bool>("--pre") { Description = "Allow prerelease versions." };
var removeJobs = JobsOption();
var removeProxy = ProxyOption();
var removeName = new Argument<string>("name") { Description = "Direct sei name." };
remove.Add(removeName);
remove.Add(removeRoot);
remove.Add(removeStore);
remove.Add(removePre);
remove.Add(removeJobs);
remove.Add(removeProxy);
remove.SetAction(parseResult => Run(
    "remove",
    IsJson(parseResult),
    () => Remove(
        parseResult.GetValue(removeRoot),
        parseResult.GetValue(removeName)!,
        parseResult.GetValue(removeStore),
        parseResult.GetValue(removePre),
        parseResult.GetValue(removeJobs) ?? DownloadSession.DefaultJobs,
        parseResult.GetValue(removeProxy),
        IsJson(parseResult))));

var update = new Command("update", "Update direct declarations and sync.");
var updateRoot = RootOption();
var updateStore = StoreOption();
var updatePre = new Option<bool>("--pre") { Description = "Allow prerelease versions." };
var updateJobs = JobsOption();
var updateProxy = ProxyOption();
var updateLatest = new Option<bool>("--latest") { Description = "Rewrite direct ranges to the latest version." };
var updateName = new Argument<string?>("name")
{
    Description = "Direct sei name; all direct declarations when omitted.",
    Arity = ArgumentArity.ZeroOrOne,
};
update.Add(updateName);
update.Add(updateRoot);
update.Add(updateStore);
update.Add(updatePre);
update.Add(updateJobs);
update.Add(updateProxy);
update.Add(updateLatest);
update.SetAction(parseResult => Run(
    "update",
    IsJson(parseResult),
    () => Update(
        parseResult.GetValue(updateRoot),
        parseResult.GetValue(updateName),
        parseResult.GetValue(updateLatest),
        parseResult.GetValue(updateStore),
        parseResult.GetValue(updatePre),
        parseResult.GetValue(updateJobs) ?? DownloadSession.DefaultJobs,
        parseResult.GetValue(updateProxy),
        IsJson(parseResult))));

var list = new Command("list", "List installed sei from the lock.");
var listRoot = RootOption();
list.Add(listRoot);
list.SetAction(parseResult => Run(
    "list",
    IsJson(parseResult),
    () => List(parseResult.GetValue(listRoot), IsJson(parseResult))));

var rollback = new Command("rollback", "Restore a previous generation.");
var rollbackRoot = RootOption();
var rollbackStore = StoreOption();
var rollbackTo = new Option<int?>("--to") { Description = "Generation number to roll back to." };
rollback.Add(rollbackRoot);
rollback.Add(rollbackStore);
rollback.Add(rollbackTo);
rollback.SetAction(parseResult => Run(
    "rollback",
    IsJson(parseResult),
    () => Rollback(
        parseResult.GetValue(rollbackRoot),
        parseResult.GetValue(rollbackStore),
        parseResult.GetValue(rollbackTo),
        IsJson(parseResult))));

var search = new Command("search", "Search sei names in the configured indexes.");
var searchRoot = RootOption();
var searchProxy = ProxyOption();
var searchTerm = new Argument<string>("term") { Description = "Name fragment to search for." };
search.Add(searchTerm);
search.Add(searchRoot);
search.Add(searchProxy);
search.SetAction(parseResult => Run(
    "search",
    IsJson(parseResult),
    () => Search(
        parseResult.GetValue(searchRoot),
        parseResult.GetValue(searchTerm)!,
        parseResult.GetValue(searchProxy),
        IsJson(parseResult))));

var info = new Command("info", "Show versions, dependencies, and artifact counts.");
var infoRoot = RootOption();
var infoProxy = ProxyOption();
var infoName = new Argument<string>("name") { Description = "Sei name." };
info.Add(infoName);
info.Add(infoRoot);
info.Add(infoProxy);
info.SetAction(parseResult => Run(
    "info",
    IsJson(parseResult),
    () => Info(
        parseResult.GetValue(infoRoot),
        parseResult.GetValue(infoName)!,
        parseResult.GetValue(infoProxy),
        IsJson(parseResult))));

var doctor = new Command("doctor", "Check the lock, store, deployment, and partial downloads.");
var doctorRoot = RootOption();
var doctorStore = StoreOption();
var doctorClean = new Option<bool>("--clean") { Description = "Delete all partial downloads." };
doctor.Add(doctorRoot);
doctor.Add(doctorStore);
doctor.Add(doctorClean);
doctor.SetAction(parseResult => Run(
    "doctor",
    IsJson(parseResult),
    () => RunDoctor(
        parseResult.GetValue(doctorRoot),
        parseResult.GetValue(doctorStore),
        parseResult.GetValue(doctorClean),
        IsJson(parseResult))));

var why = new Command("why", "Explain why a sei is in the dependency graph.");
var whyRoot = RootOption();
var whyName = new Argument<string>("name") { Description = "Sei name." };
why.Add(whyName);
why.Add(whyRoot);
why.SetAction(parseResult => Run(
    "why",
    IsJson(parseResult),
    () => Why(
        parseResult.GetValue(whyRoot),
        parseResult.GetValue(whyName)!,
        IsJson(parseResult))));

var outdated = new Command("outdated", "Report sei that have newer versions.");
var outdatedRoot = RootOption();
var outdatedProxy = ProxyOption();
var outdatedPre = new Option<bool>("--pre") { Description = "Allow prerelease versions." };
outdated.Add(outdatedRoot);
outdated.Add(outdatedProxy);
outdated.Add(outdatedPre);
outdated.SetAction(parseResult => Run(
    "outdated",
    IsJson(parseResult),
    () => Outdated(
        parseResult.GetValue(outdatedRoot),
        parseResult.GetValue(outdatedProxy),
        parseResult.GetValue(outdatedPre),
        IsJson(parseResult))));

var source = new Command("source", "Manage configured sources.");
var sourceList = new Command("list", "List configured sources.");
var sourceListRoot = RootOption();
sourceList.Add(sourceListRoot);
sourceList.SetAction(parseResult => Run(
    "source_list",
    IsJson(parseResult),
    () => SourceListCommand(parseResult.GetValue(sourceListRoot), IsJson(parseResult))));

var sourceAdd = new Command("add", "Add a source.");
var sourceAddRoot = RootOption();
var sourceAddTokenEnv = new Option<string?>("--token-env") { Description = "Environment variable holding a bearer token." };
var sourceAddTokenHosts = new Option<string[]>("--token-host")
{
    Description = "Extra host allowed to receive the bearer token (repeatable).",
    Arity = ArgumentArity.OneOrMore,
    AllowMultipleArgumentsPerToken = true,
};
var sourceAddId = new Argument<string>("id") { Description = "Source id." };
var sourceAddIndex = new Argument<string>("index") { Description = "Index URL or local path." };
sourceAdd.Add(sourceAddId);
sourceAdd.Add(sourceAddIndex);
sourceAdd.Add(sourceAddRoot);
sourceAdd.Add(sourceAddTokenEnv);
sourceAdd.Add(sourceAddTokenHosts);
sourceAdd.SetAction(parseResult => Run(
    "source_add",
    IsJson(parseResult),
    () => SourceAddCommand(
        parseResult.GetValue(sourceAddRoot),
        parseResult.GetValue(sourceAddId)!,
        parseResult.GetValue(sourceAddIndex)!,
        parseResult.GetValue(sourceAddTokenHosts),
        parseResult.GetValue(sourceAddTokenEnv),
        IsJson(parseResult))));

var sourceRemove = new Command("remove", "Remove a source.");
var sourceRemoveRoot = RootOption();
var sourceRemoveId = new Argument<string>("id") { Description = "Source id." };
sourceRemove.Add(sourceRemoveId);
sourceRemove.Add(sourceRemoveRoot);
sourceRemove.SetAction(parseResult => Run(
    "source_remove",
    IsJson(parseResult),
    () => SourceRemoveCommand(
        parseResult.GetValue(sourceRemoveRoot),
        parseResult.GetValue(sourceRemoveId)!,
        IsJson(parseResult))));

source.Add(sourceList);
source.Add(sourceAdd);
source.Add(sourceRemove);

var config = new Command("config", "Read or write install-root configuration.");
var configGet = new Command("get", "Read a configuration value.");
var configGetRoot = RootOption();
var configGetKey = new Argument<string>("key") { Description = "Configuration key (rid)." };
configGet.Add(configGetKey);
configGet.Add(configGetRoot);
configGet.SetAction(parseResult => Run(
    "config_get",
    IsJson(parseResult),
    () => ConfigGetCommand(
        parseResult.GetValue(configGetRoot),
        parseResult.GetValue(configGetKey)!,
        IsJson(parseResult))));

var configSet = new Command("set", "Write a configuration value.");
var configSetRoot = RootOption();
var configSetKey = new Argument<string>("key") { Description = "Configuration key (rid)." };
var configSetValue = new Argument<string>("value") { Description = "Value; empty removes the key." };
configSet.Add(configSetKey);
configSet.Add(configSetValue);
configSet.Add(configSetRoot);
configSet.SetAction(parseResult => Run(
    "config_set",
    IsJson(parseResult),
    () => ConfigSetCommand(
        parseResult.GetValue(configSetRoot),
        parseResult.GetValue(configSetKey)!,
        parseResult.GetValue(configSetValue)!,
        IsJson(parseResult))));

config.Add(configGet);
config.Add(configSet);

var cache = new Command("cache", "Inspect or clean the content-addressed store.");
var cacheStatus = new Command("status", "Show store artifact and partial counts.");
var cacheStatusStore = StoreOption();
cacheStatus.Add(cacheStatusStore);
cacheStatus.SetAction(parseResult => Run(
    "cache_status",
    IsJson(parseResult),
    () => CacheStatusCommand(parseResult.GetValue(cacheStatusStore), IsJson(parseResult))));

var cacheVerify = new Command("verify", "Verify stored artifacts.");
var cacheVerifyStore = StoreOption();
cacheVerify.Add(cacheVerifyStore);
cacheVerify.SetAction(parseResult => Run(
    "cache_verify",
    IsJson(parseResult),
    () => CacheVerifyCommand(parseResult.GetValue(cacheVerifyStore), IsJson(parseResult))));

var cacheClean = new Command("clean", "Remove artifacts not referenced by this install root.");
var cacheCleanRoot = RootOption();
var cacheCleanStore = StoreOption();
cacheClean.Add(cacheCleanRoot);
cacheClean.Add(cacheCleanStore);
cacheClean.SetAction(parseResult => Run(
    "cache_clean",
    IsJson(parseResult),
    () => CacheCleanCommand(
        parseResult.GetValue(cacheCleanRoot),
        parseResult.GetValue(cacheCleanStore),
        IsJson(parseResult))));

cache.Add(cacheStatus);
cache.Add(cacheVerify);
cache.Add(cacheClean);

var index = new Command("index", "Inspect static index repositories.");
var indexVerify = new Command("verify", "Validate a static index directory.");
var indexVerifyPath = new Argument<string?>("path")
{
    Description = "Index directory (defaults to the current directory).",
    Arity = ArgumentArity.ZeroOrOne,
};
var indexVerifyArtifacts = new Option<bool>("--check-artifacts")
{
    Description = "Download artifacts and verify size, SHA-256, and sei.json.",
};
var indexVerifyStrict = new Option<bool>("--strict")
{
    Description = "Treat warnings as errors.",
};
var indexVerifyProxy = ProxyOption();
indexVerify.Add(indexVerifyPath);
indexVerify.Add(indexVerifyArtifacts);
indexVerify.Add(indexVerifyStrict);
indexVerify.Add(indexVerifyProxy);
indexVerify.SetAction(parseResult => Run(
    "index_verify",
    IsJson(parseResult),
    () => IndexVerifyCommand(
        parseResult.GetValue(indexVerifyPath),
        parseResult.GetValue(indexVerifyArtifacts),
        parseResult.GetValue(indexVerifyStrict),
        parseResult.GetValue(indexVerifyProxy),
        IsJson(parseResult))));
index.Add(indexVerify);

var help = new Command("help", "Show help.");
help.SetAction(_ => rootCommand.Parse(["--help"]).Invoke());

rootCommand.Add(init);
rootCommand.Add(sync);
rootCommand.Add(add);
rootCommand.Add(remove);
rootCommand.Add(update);
rootCommand.Add(why);
rootCommand.Add(outdated);
rootCommand.Add(list);
rootCommand.Add(rollback);
rootCommand.Add(search);
rootCommand.Add(info);
rootCommand.Add(doctor);
rootCommand.Add(source);
rootCommand.Add(config);
rootCommand.Add(cache);
rootCommand.Add(index);
rootCommand.Add(help);
rootCommand.SetAction(_ => rootCommand.Parse(["--help"]).Invoke());

var parseResult = rootCommand.Parse(args);
Log.Verbose = parseResult.GetValue<bool>("--verbose");
Log.Quiet = parseResult.GetValue<bool>("--quiet");
if (parseResult.Errors.Count > 0)
{
    if (args.Contains("--json", StringComparer.Ordinal))
    {
        WriteJson(
            CliJsonContext.Default.ErrorEnvelope,
            new ErrorEnvelope(1, FirstCommand(parseResult), new ErrorData("usage", parseResult.Errors[0].Message)));
    }
    else
    {
        foreach (var error in parseResult.Errors)
        {
            Console.Error.WriteLine($"sk: {error.Message}");
        }
    }

    return 2;
}

return parseResult.Invoke();

static bool IsJson(ParseResult parseResult) => parseResult.GetValue<bool>("--json");

static int Run(string command, bool json, Func<int> action)
{
    try
    {
        return action();
    }
    catch (SeikoException ex)
    {
        return Fail(command, json, ex.Code ?? "internal", ex.Message, ex.Code == "usage" ? 2 : 1);
    }
    catch (IOException ex)
    {
        return FailException(command, json, "io", ex);
    }
    catch (UnauthorizedAccessException ex)
    {
        return FailException(command, json, "io", ex);
    }
    catch (Exception ex)
    {
        return FailException(command, json, "internal", ex);
    }
}

static int FailException(string command, bool json, string code, Exception ex)
{
    if (Log.Verbose && !json)
    {
        Console.Error.WriteLine(ex.Demystify());
        return 1;
    }

    return Fail(command, json, code, ex.Message, 1);
}

static int Fail(string command, bool json, string code, string message, int exitCode)
{
    if (json)
    {
        WriteJson(
            CliJsonContext.Default.ErrorEnvelope,
            new ErrorEnvelope(1, command, new ErrorData(code, message)));
    }
    else
    {
        Console.Error.WriteLine($"sk: {message}");
    }

    return exitCode;
}

static void WriteJson<T>(JsonTypeInfo<T> typeInfo, T value)
    => Console.WriteLine(JsonSerializer.Serialize(value, typeInfo));

static string FirstCommand(ParseResult parseResult)
{
    var command = parseResult.CommandResult?.Command;
    var names = new List<string>();
    while (command is not null && command is not RootCommand)
    {
        names.Add(command.Name);
        command = command.Parents.OfType<Command>().FirstOrDefault();
    }

    names.Reverse();
    return names.Count == 0 ? "sk" : string.Join("_", names);
}

static Option<string?> RootOption()
    => new("--root") { Description = "Install root (defaults to the nearest seiko.conf)." };

static Option<string?> StoreOption()
    => new("--store") { Description = "Content-addressed store directory." };

static Option<string?> ProxyOption()
    => new("--proxy") { Description = "HTTP proxy URL; defaults to the system proxy." };

static Option<int?> JobsOption()
    => new("--jobs") { Description = "Maximum concurrent HTTP requests (default 4)." };

static int Sync(
    string? rootOption,
    string? store,
    bool pre,
    bool frozen,
    bool locked,
    int jobs,
    string? proxy,
    bool json)
{
    if (frozen && locked)
    {
        throw new SeikoException("--frozen and --locked are mutually exclusive", "usage");
    }

    var root = rootOption ?? FindRoot();
    var mode = frozen ? SyncMode.Frozen : locked ? SyncMode.Locked : SyncMode.Normal;
    var showProgress = !json && !Log.Quiet && ProgressDisplay.IsSupported();
    SyncResult result;
    if (showProgress)
    {
        using var display = new ProgressDisplay();
        result = new SeikoClient().Sync(root, store, pre, mode, jobs, proxy, display, Log.Debug);
    }
    else
    {
        result = new SeikoClient().Sync(root, store, pre, mode, jobs, proxy, log: Log.Debug);
    }
    if (json)
    {
        WriteJson(
            CliJsonContext.Default.SyncEnvelope,
            new SyncEnvelope(1, "sync", new SyncData(result.SeiInstalled, result.FilesDeployed, mode.ToString())));
        return 0;
    }

    PrintSyncResult(result);
    return 0;
}

static int Add(
    string? rootOption,
    string specification,
    string? store,
    bool pre,
    int jobs,
    string? proxy,
    bool json)
    => RunMutation(
        "add",
        json,
        (progress, log) => new SeikoClient().Add(
            rootOption ?? FindRoot(),
            specification,
            store,
            pre,
            jobs,
            proxy,
            progress,
            log));

static int Remove(
    string? rootOption,
    string name,
    string? store,
    bool pre,
    int jobs,
    string? proxy,
    bool json)
    => RunMutation(
        "remove",
        json,
        (progress, log) => new SeikoClient().Remove(
            rootOption ?? FindRoot(),
            name,
            store,
            pre,
            jobs,
            proxy,
            progress,
            log));

static int Update(
    string? rootOption,
    string? name,
    bool latest,
    string? store,
    bool pre,
    int jobs,
    string? proxy,
    bool json)
    => RunMutation(
        "update",
        json,
        (progress, log) => new SeikoClient().Update(
            rootOption ?? FindRoot(),
            name,
            latest,
            store,
            pre,
            jobs,
            proxy,
            progress,
            log));

static int Why(string? rootOption, string name, bool json)
{
    var root = rootOption ?? FindRoot();
    var chains = new SeikoClient().Why(root, name);
    if (json)
    {
        WriteJson(
            CliJsonContext.Default.WhyEnvelope,
            new WhyEnvelope(1, "why", new WhyData(name, chains.Select(chain => chain.ToList()).ToList())));
        return 0;
    }

    if (chains.Count == 1 && chains[0].Count == 1)
    {
        Console.WriteLine($"{name} is a direct dependency");
        return 0;
    }

    foreach (var chain in chains)
    {
        Console.WriteLine(string.Join(" -> ", chain));
    }

    return 0;
}

static int Outdated(string? rootOption, string? proxy, bool pre, bool json)
{
    var root = rootOption ?? FindRoot();
    var items = new SeikoClient().Outdated(root, proxy, pre);
    if (json)
    {
        WriteJson(CliJsonContext.Default.OutdatedEnvelope, new OutdatedEnvelope(1, "outdated", items.ToList()));
        return 0;
    }

    if (items.Count == 0)
    {
        Console.WriteLine("everything is up to date");
        return 0;
    }

    Console.WriteLine("NAME	CURRENT	LATEST-IN-RANGE	LATEST");
    foreach (var item in items)
    {
        Console.WriteLine($"{item.Name}	{item.Current}	{item.LatestInRange ?? "-"}	{item.Latest}");
    }

    return 0;
}

static int SourceListCommand(string? rootOption, bool json)
{
    var root = rootOption ?? FindRoot();
    var sources = new SeikoClient().ListSources(root);
    PrintSources("source_list", sources, json);
    return 0;
}

static int SourceAddCommand(
    string? rootOption,
    string id,
    string index,
    string[]? tokenHosts,
    string? tokenEnv,
    bool json)
{
    var root = rootOption ?? FindRoot();
    var sources = new SeikoClient().SourceAdd(root, id, index, tokenEnv, tokenHosts);
    PrintSources("source_add", sources, json);
    return 0;
}

static int SourceRemoveCommand(string? rootOption, string id, bool json)
{
    var root = rootOption ?? FindRoot();
    var sources = new SeikoClient().SourceRemove(root, id);
    PrintSources("source_remove", sources, json);
    return 0;
}

static void PrintSources(string command, IReadOnlyList<SourceConfig> sources, bool json)
{
    var items = sources
        .Select(source => new SourceItem(source.Id, source.Index, source.TokenHosts))
        .ToList();
    if (json)
    {
        WriteJson(CliJsonContext.Default.SourceEnvelope, new SourceEnvelope(1, command, items));
        return;
    }

    Console.WriteLine("ID\tINDEX\tTOKEN HOSTS");
    foreach (var source in items)
    {
        var hosts = source.TokenHosts is null ? "" : string.Join(",", source.TokenHosts);
        Console.WriteLine($"{source.Id}\t{source.Index}\t{hosts}");
    }
}

static int ConfigGetCommand(string? rootOption, string key, bool json)
{
    var root = rootOption ?? FindRoot();
    var value = new SeikoClient().ConfigGet(root, key);
    PrintConfig("config_get", key, value, json);
    return 0;
}

static int ConfigSetCommand(string? rootOption, string key, string value, bool json)
{
    var root = rootOption ?? FindRoot();
    var stored = new SeikoClient().ConfigSet(root, key, value);
    PrintConfig("config_set", key, stored, json);
    return 0;
}

static void PrintConfig(string command, string key, string? value, bool json)
{
    if (json)
    {
        WriteJson(
            CliJsonContext.Default.ConfigEnvelope,
            new ConfigEnvelope(1, command, new ConfigData(value)));
        return;
    }

    Console.WriteLine(value is null ? $"{key} is not set" : $"{key} = {value}");
}

static int CacheStatusCommand(string? store, bool json)
{
    var status = new SeikoClient().CacheStatus(store);
    if (json)
    {
        WriteJson(CliJsonContext.Default.CacheStatusEnvelope, new CacheStatusEnvelope(1, "cache_status", status));
        return 0;
    }

    Console.WriteLine($"artifacts: {status.Artifacts}");
    Console.WriteLine($"bytes: {status.Bytes}");
    Console.WriteLine($"partials: {status.PartialItems}");
    return 0;
}

static int CacheVerifyCommand(string? store, bool json)
{
    var findings = new SeikoClient().CacheVerify(store);
    var hasError = findings.Any(finding => finding.Severity == "error");
    if (json)
    {
        WriteJson(CliJsonContext.Default.CacheVerifyEnvelope, new CacheVerifyEnvelope(1, "cache_verify", findings.ToList()));
        return hasError ? 1 : 0;
    }

    foreach (var finding in findings)
    {
        var path = finding.Path is null ? "" : $" ({finding.Path})";
        Console.WriteLine($"{finding.Severity} {finding.Code}: {finding.Message}{path}");
    }

    if (findings.Count == 0)
    {
        Console.WriteLine("no problems found");
    }

    return hasError ? 1 : 0;
}

static int CacheCleanCommand(string? rootOption, string? store, bool json)
{
    var root = rootOption ?? FindRoot();
    var result = new SeikoClient().CacheClean(root, store);
    if (json)
    {
        WriteJson(CliJsonContext.Default.CacheCleanEnvelope, new CacheCleanEnvelope(1, "cache_clean", result));
        return 0;
    }

    Console.WriteLine($"removed {result.Removed} artifact(s), freed {result.Bytes} bytes");
    return 0;
}

static int RunMutation(
    string command,
    bool json,
    Func<IProgress<SyncProgress>?, Action<string>, SyncResult> action)
{
    var showProgress = !json && !Log.Quiet && ProgressDisplay.IsSupported();
    SyncResult result;
    if (showProgress)
    {
        using var display = new ProgressDisplay();
        result = action(display, Log.Debug);
    }
    else
    {
        result = action(null, Log.Debug);
    }

    if (json)
    {
        WriteJson(
            CliJsonContext.Default.SyncEnvelope,
            new SyncEnvelope(1, command, new SyncData(result.SeiInstalled, result.FilesDeployed, SyncMode.Normal.ToString())));
    }
    else
    {
        PrintSyncResult(result);
    }

    return 0;
}

static void PrintSyncResult(SyncResult result)
{
    Console.WriteLine($"resolved {result.SeiInstalled} sei, deployed {result.FilesDeployed} file(s)");
    foreach (var sei in result.Lock.Sei)
    {
        Console.WriteLine($"  {sei.Name} {sei.Version}{(sei.Direct ? " (direct)" : "")}");
    }
}

static int List(string? rootOption, bool json)
{
    var root = rootOption ?? FindRoot();
    var lockFile = new SeikoClient().ReadLock(root);
    var items = new List<ListItem>();
    if (lockFile is not null)
    {
        foreach (var sei in lockFile.Sei)
        {
            var selectors = sei.Artifact.Selectors is null
                ? null
                : sei.Artifact.Selectors
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            items.Add(new ListItem(sei.Name, sei.Version, sei.Source, sei.Direct, selectors));
        }
    }

    if (json)
    {
        WriteJson(CliJsonContext.Default.ListEnvelope, new ListEnvelope(1, "list", items));
        return 0;
    }

    if (lockFile is null)
    {
        Console.WriteLine("no lock; run `sk sync`");
        return 0;
    }

    Console.WriteLine("NAME\tVERSION\tDIRECT\tSOURCE\tSELECTORS");
    foreach (var sei in lockFile.Sei)
    {
        var selectors = sei.Artifact.Selectors is null
            ? "-"
            : string.Join(",", sei.Artifact.Selectors.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
        Console.WriteLine($"{sei.Name}\t{sei.Version}\t{(sei.Direct ? "yes" : "no")}\t{sei.Source}\t{selectors}");
    }

    return 0;
}

static int Search(string? rootOption, string term, string? proxy, bool json)
{
    var root = rootOption ?? FindRoot();
    var results = new SeikoClient().Search(root, term, proxy);
    if (json)
    {
        WriteJson(CliJsonContext.Default.SearchEnvelope, new SearchEnvelope(1, "search", results.ToList()));
        return 0;
    }

    Console.WriteLine("NAME\tVERSION\tSOURCE");
    foreach (var result in results)
    {
        Console.WriteLine($"{result.Name}\t{result.Version}\t{result.Source}");
    }

    return 0;
}

static int Info(string? rootOption, string name, string? proxy, bool json)
{
    var root = rootOption ?? FindRoot();
    var info = new SeikoClient().Info(root, name, proxy);
    if (json)
    {
        var data = new InfoData(
            info.Name,
            info.Source,
            info.Versions
                .Select(version => new InfoVersion(
                    version.Version,
                    version.Yanked,
                    version.Deprecated,
                    version.ArtifactCount,
                    version.Dependencies.ToList()))
                .ToList());
        WriteJson(CliJsonContext.Default.InfoEnvelope, new InfoEnvelope(1, "info", data));
        return 0;
    }

    Console.WriteLine($"{info.Name} ({info.Source})");
    foreach (var version in info.Versions)
    {
        var flags = version.Yanked ? " yanked" : "";
        if (!string.IsNullOrWhiteSpace(version.Deprecated))
        {
            flags += $" deprecated: {version.Deprecated}";
        }

        Console.WriteLine($"  {version.Version}{flags}  deps={version.Dependencies.Count} artifacts={version.ArtifactCount}");
    }

    return 0;
}

static int Rollback(string? rootOption, string? store, int? toGeneration, bool json)
{
    if (toGeneration is < 1)
    {
        throw new SeikoException("--to requires a positive generation number", "usage");
    }

    var root = rootOption ?? FindRoot();
    var result = new SeikoClient().Rollback(root, store, toGeneration);
    if (json)
    {
        WriteJson(
            CliJsonContext.Default.RollbackEnvelope,
            new RollbackEnvelope(1, "rollback", new RollbackData(result.Generation, result.SeiInstalled, result.FilesDeployed)));
        return 0;
    }

    Console.WriteLine($"rolled back to generation {result.Generation}: {result.SeiInstalled} sei, {result.FilesDeployed} file(s)");
    return 0;
}

static int Init(string? rootOption, bool json)
{
    var root = Path.GetFullPath(rootOption ?? Directory.GetCurrentDirectory());
    Directory.CreateDirectory(root);
    var path = Path.Combine(root, "seiko.conf");
    if (File.Exists(path))
    {
        throw new SeikoException($"{path} already exists", "io");
    }

    File.WriteAllText(path, InitTemplate.Create() + "\n");
    if (json)
    {
        WriteJson(CliJsonContext.Default.InitEnvelope, new InitEnvelope(1, "init", new InitData(path)));
        return 0;
    }

    Console.WriteLine($"created {path}");
    return 0;
}

static int IndexVerifyCommand(
    string? pathOption,
    bool checkArtifacts,
    bool strict,
    string? proxy,
    bool json)
{
    var directory = Path.GetFullPath(pathOption ?? Directory.GetCurrentDirectory());
    var result = IndexVerifier.Verify(directory, checkArtifacts, proxy, Log.Debug);
    var failed = result.Errors > 0 || (strict && result.Warnings > 0);
    if (json)
    {
        WriteJson(
            CliJsonContext.Default.IndexVerifyEnvelope,
            new IndexVerifyEnvelope(1, "index_verify", result));
        return failed ? 1 : 0;
    }

    foreach (var finding in result.Findings)
    {
        var path = finding.Path is null ? "" : $" ({finding.Path})";
        Console.WriteLine($"{finding.Severity} {finding.Code}: {finding.Message}{path}");
    }

    var checkedText = result.CheckedArtifacts > 0
        ? $", {result.CheckedArtifacts} artifact(s) checked"
        : "";
    Console.WriteLine(
        $"{result.Sei} sei, {result.Versions} version(s), {result.Artifacts} artifact(s){checkedText}; "
        + $"{result.Errors} error(s), {result.Warnings} warning(s)");
    return failed ? 1 : 0;
}

static int RunDoctor(string? rootOption, string? store, bool clean, bool json)
{
    var root = rootOption ?? FindRoot();
    var result = new Doctor().Run(root, store, clean);
    var hasError = result.Findings.Any(finding => finding.Severity == "error");
    if (json)
    {
        WriteJson(CliJsonContext.Default.DoctorEnvelope, new DoctorEnvelope(1, "doctor", result));
        return hasError ? 1 : 0;
    }

    foreach (var finding in result.Findings)
    {
        var path = finding.Path is null ? "" : $" ({finding.Path})";
        Console.WriteLine($"{finding.Severity} {finding.Code}: {finding.Message}{path}");
    }

    if (result.CleanCount > 0)
    {
        Console.WriteLine($"cleaned {result.CleanCount} partial item(s)");
    }

    if (result.Findings.Count == 0)
    {
        Console.WriteLine("no problems found");
    }

    return hasError ? 1 : 0;
}

static string FindRoot()
{
    var current = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "seiko.conf")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    return Directory.GetCurrentDirectory();
}
