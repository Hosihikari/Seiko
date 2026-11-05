using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Seiko;
using Seiko.CommandLine;

// Localised text may contain scripts the console codepage cannot encode
// (Arabic on a GBK console); UTF-8 is the one encoding all catalogs share.
try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch (IOException)
{
}

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
var offlineOption = new Option<bool>("--offline")
{
    Description = "Use only locked, stored, and cached metadata; never touch the network.",
    Recursive = true,
};
var diagnosticsOption = new Option<bool>("--diagnostics")
{
    Description = "Show relation and constraint details.",
    Recursive = true,
};
var colorOption = new Option<string?>("--color")
{
    Description = "Colour output: auto (default), always, or never. NO_COLOR is honoured.",
    Recursive = true,
};
rootCommand.Add(jsonOption);
rootCommand.Add(verboseOption);
rootCommand.Add(quietOption);
rootCommand.Add(offlineOption);
rootCommand.Add(diagnosticsOption);
rootCommand.Add(colorOption);

var init = new Command("init", "Write a seiko.conf template.\nExamples:\nsk init --root ./server" + "\n" + "Docs: https://hoshi.srcz.one/seiko/getting-started/");
var initRoot = RootOption();
init.Add(initRoot);
init.SetAction(parseResult => Run(
    "init",
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => Init(parseResult.GetValue(initRoot), IsJson(parseResult))));

var sync = new Command("sync", "Resolve sei and deploy files into the install root.\nExamples:\nsk sync --dry-run\nsk sync --frozen" + "\n" + "Docs: https://hoshi.srcz.one/seiko/commands/");
var syncRoot = RootOption();
var syncStore = StoreOption();
var syncPre = new Option<bool>("--pre") { Description = "Allow prerelease versions." };
var syncFrozen = new Option<bool>("--frozen") { Description = "Replay seiko.lock.json without resolving." };
var syncLocked = new Option<bool>("--locked") { Description = "Fail if the lock is out of date." };
var syncJobs = new Option<int?>("--jobs") { Description = "Maximum concurrent HTTP requests (default 4)." };
var syncProxy = ProxyOption();
var syncLatestInstallable = new Option<bool>("--latest-installable")
{
    Description = "Pick the highest version that has an artifact for the resolved graph.",
};
var syncAllowCritical = new Option<bool>("--allow-critical")
{
    Description = "Allow resolving versions with a critical advisory.",
};
var syncDryRun = DryRunOption();
var syncAdopt = AdoptOption();
var syncForce = ForceOption();
sync.Add(syncRoot);
sync.Add(syncStore);
sync.Add(syncPre);
sync.Add(syncFrozen);
sync.Add(syncLocked);
sync.Add(syncJobs);
sync.Add(syncProxy);
sync.Add(syncLatestInstallable);
sync.Add(syncAllowCritical);
sync.Add(syncDryRun);
sync.Add(syncAdopt);
sync.Add(syncForce);
sync.SetAction(parseResult => Run(
    "sync",
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => Sync(
        parseResult.GetValue(syncRoot),
        parseResult.GetValue(syncStore),
        parseResult.GetValue(syncPre),
        parseResult.GetValue(syncFrozen),
        parseResult.GetValue(syncLocked),
        parseResult.GetValue(syncJobs) ?? DownloadSession.DefaultJobs,
        parseResult.GetValue(syncProxy),
        parseResult.GetValue(syncLatestInstallable),
        parseResult.GetValue(syncAllowCritical),
        parseResult.GetValue(syncDryRun),
        parseResult.GetValue(syncAdopt),
        parseResult.GetValue(syncForce),
        IsOffline(parseResult),
        IsDiagnostics(parseResult),
        IsJson(parseResult))));
sync.Aliases.Add("install");

var add = new Command("add", "Add a direct sei declaration and sync.\nExamples:\nsk add hello\nsk add hello@[1.2.0,2.0.0) --dry-run" + "\n" + "Docs: https://hoshi.srcz.one/seiko/commands/");
var addRoot = RootOption();
var addStore = StoreOption();
var addPre = new Option<bool>("--pre") { Description = "Allow prerelease versions." };
var addJobs = JobsOption();
var addProxy = ProxyOption();
var addDryRun = DryRunOption();
var addAdopt = AdoptOption();
var addForce = ForceOption();
var addLatestInstallable = new Option<bool>("--latest-installable")
{
    Description = "Pick the highest version that has an artifact for the resolved graph.",
};
var addAllowCritical = new Option<bool>("--allow-critical")
{
    Description = "Allow resolving versions with a critical advisory.",
};
var addSpec = new Argument<string[]>("spec")
{
    Description = "One or more sei names with optional @range.",
    Arity = ArgumentArity.OneOrMore,
};
var addRemove = new Option<string[]>("--remove")
{
    Description = "Direct declarations to remove in the same operation.",
    Arity = ArgumentArity.OneOrMore,
};
add.Add(addSpec);
add.Add(addRemove);
add.Add(addRoot);
add.Add(addStore);
add.Add(addPre);
add.Add(addJobs);
add.Add(addProxy);
add.Add(addLatestInstallable);
add.Add(addAllowCritical);
add.Add(addDryRun);
add.Add(addAdopt);
add.Add(addForce);
add.SetAction(parseResult => Run(
    "add",
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => Add(
        parseResult.GetValue(addRoot),
        parseResult.GetValue(addSpec)!,
        parseResult.GetValue(addRemove),
        parseResult.GetValue(addStore),
        parseResult.GetValue(addPre),
        parseResult.GetValue(addJobs) ?? DownloadSession.DefaultJobs,
        parseResult.GetValue(addProxy),
        parseResult.GetValue(addLatestInstallable),
        parseResult.GetValue(addAllowCritical),
        parseResult.GetValue(addDryRun),
        parseResult.GetValue(addAdopt),
        parseResult.GetValue(addForce),
        IsOffline(parseResult),
        IsDiagnostics(parseResult),
        IsJson(parseResult))));

var remove = new Command("remove", "Remove a direct sei declaration and sync.\nExamples:\nsk remove hello" + "\n" + "Docs: https://hoshi.srcz.one/seiko/commands/");
var removeRoot = RootOption();
var removeStore = StoreOption();
var removePre = new Option<bool>("--pre") { Description = "Allow prerelease versions." };
var removeJobs = JobsOption();
var removeProxy = ProxyOption();
var removeDryRun = DryRunOption();
var removeAdopt = AdoptOption();
var removeForce = ForceOption();
var removePurge = new Option<bool>("--purge")
{
    Description = "Also delete config/seiko/<name>.conf and data/seiko/<name>/.",
};
var removeAllowCritical = new Option<bool>("--allow-critical")
{
    Description = "Allow resolving versions with a critical advisory.",
};
var removeName = new Argument<string>("name") { Description = "Direct sei name." };
remove.Add(removeName);
remove.Add(removeRoot);
remove.Add(removeStore);
remove.Add(removePre);
remove.Add(removeJobs);
remove.Add(removeProxy);
remove.Add(removeAllowCritical);
remove.Add(removeDryRun);
remove.Add(removePurge);
remove.Add(removeAdopt);
remove.Add(removeForce);
remove.SetAction(parseResult => Run(
    "remove",
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => Remove(
        parseResult.GetValue(removeRoot),
        parseResult.GetValue(removeName)!,
        parseResult.GetValue(removeStore),
        parseResult.GetValue(removePre),
        parseResult.GetValue(removeJobs) ?? DownloadSession.DefaultJobs,
        parseResult.GetValue(removeProxy),
        parseResult.GetValue(removeAllowCritical),
        parseResult.GetValue(removeDryRun),
        parseResult.GetValue(removePurge),
        parseResult.GetValue(removeAdopt),
        parseResult.GetValue(removeForce),
        IsOffline(parseResult),
        IsDiagnostics(parseResult),
        IsJson(parseResult))));

var update = new Command("update", "Update direct declarations and sync.\nExamples:\nsk update --dry-run\nsk update hello --latest" + "\n" + "Docs: https://hoshi.srcz.one/seiko/commands/");
var updateRoot = RootOption();
var updateStore = StoreOption();
var updatePre = new Option<bool>("--pre") { Description = "Allow prerelease versions." };
var updateJobs = JobsOption();
var updateProxy = ProxyOption();
var updateDryRun = DryRunOption();
var updateAdopt = AdoptOption();
var updateForce = ForceOption();
var updateLatest = new Option<bool>("--latest") { Description = "Rewrite direct ranges to the latest version." };
var updateLatestInstallable = new Option<bool>("--latest-installable")
{
    Description = "Pick the highest version that has an artifact for the resolved graph.",
};
var updateAllowCritical = new Option<bool>("--allow-critical")
{
    Description = "Allow resolving versions with a critical advisory.",
};
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
update.Add(updateLatestInstallable);
update.Add(updateAllowCritical);
update.Add(updateDryRun);
update.Add(updateAdopt);
update.Add(updateForce);
update.SetAction(parseResult => Run(
    "update",
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => Update(
        parseResult.GetValue(updateRoot),
        parseResult.GetValue(updateName),
        parseResult.GetValue(updateLatest),
        parseResult.GetValue(updateStore),
        parseResult.GetValue(updatePre),
        parseResult.GetValue(updateJobs) ?? DownloadSession.DefaultJobs,
        parseResult.GetValue(updateProxy),
        parseResult.GetValue(updateLatestInstallable),
        parseResult.GetValue(updateAllowCritical),
        parseResult.GetValue(updateDryRun),
        parseResult.GetValue(updateAdopt),
        parseResult.GetValue(updateForce),
        IsOffline(parseResult),
        IsDiagnostics(parseResult),
        IsJson(parseResult))));

var list = new Command("list", "List installed sei from the lock.");
var listRoot = RootOption();
list.Add(listRoot);
list.SetAction(parseResult => Run(
    "list",
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => List(parseResult.GetValue(listRoot), IsJson(parseResult))));

var rollback = new Command("rollback", "Restore a previous generation.\nExamples:\nsk generation list\nsk rollback --dry-run\nsk rollback --to 3" + "\n" + "Docs: https://hoshi.srcz.one/seiko/commands/");
var rollbackRoot = RootOption();
var rollbackStore = StoreOption();
var rollbackTo = new Option<int?>("--to") { Description = "Generation number to roll back to." };
var rollbackDryRun = new Option<bool>("--dry-run")
{
    Description = "Show the diff to the target generation without changing anything.",
};
var rollbackProxy = ProxyOption();
rollback.Add(rollbackRoot);
rollback.Add(rollbackStore);
rollback.Add(rollbackTo);
rollback.Add(rollbackDryRun);
rollback.Add(rollbackProxy);
rollback.SetAction(parseResult => Run(
    "rollback",
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => Rollback(
        parseResult.GetValue(rollbackRoot),
        parseResult.GetValue(rollbackStore),
        parseResult.GetValue(rollbackTo),
        parseResult.GetValue(rollbackProxy),
        IsOffline(parseResult),
        IsDiagnostics(parseResult),
        IsJson(parseResult),
        parseResult.GetValue(rollbackDryRun))));

var generation = new Command("generation", "Inspect deployment generations.");
var generationList = new Command("list", "List generations, newest first.");
var generationRoot = RootOption();
var generationStore = StoreOption();
generationList.Add(generationRoot);
generationList.Add(generationStore);
generationList.SetAction(parseResult => Run(
    "generation_list",
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => Generations(
        parseResult.GetValue(generationRoot),
        parseResult.GetValue(generationStore),
        IsJson(parseResult))));
generation.Add(generationList);

var search = new Command("search", "Search sei names, descriptions, and tags in the configured indexes.\nExamples:\nsk search server\nsk search server --tag hosting --limit 10" + "\n" + "Docs: https://hoshi.srcz.one/seiko/commands/");
var searchRoot = RootOption();
var searchProxy = ProxyOption();
var searchTerms = new Argument<string[]>("terms")
{
    Description = "Name, description, or tag fragments to search for.",
    Arity = ArgumentArity.OneOrMore,
};
var searchTag = new Option<string?>("--tag") { Description = "Only sei with this exact tag." };
var searchLicense = new Option<string?>("--license") { Description = "Only sei with this exact SPDX license." };
var searchLimit = new Option<int?>("--limit") { Description = "Maximum number of results." };
search.Add(searchTerms);
search.Add(searchTag);
search.Add(searchLicense);
search.Add(searchLimit);
search.Add(searchRoot);
search.Add(searchProxy);
search.SetAction(parseResult => Run(
    "search",
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => Search(
        parseResult.GetValue(searchRoot),
        parseResult.GetValue(searchTerms) ?? [],
        parseResult.GetValue(searchProxy),
        IsOffline(parseResult),
        IsDiagnostics(parseResult),
        IsJson(parseResult),
        parseResult.GetValue(searchTag),
        parseResult.GetValue(searchLicense),
        parseResult.GetValue(searchLimit))));

var info = new Command("info", "Show project, versions, dependencies, and artifact counts.\nExamples:\nsk info hello\nsk info hello --versions" + "\n" + "Docs: https://hoshi.srcz.one/seiko/commands/");
var infoRoot = RootOption();
var infoProxy = ProxyOption();
var infoVersions = new Option<bool>("--versions") { Description = "Show every version, not only notable ones." };
var infoName = new Argument<string>("name") { Description = "Sei name." };
info.Add(infoName);
info.Add(infoVersions);
info.Add(infoRoot);
info.Add(infoProxy);
info.SetAction(parseResult => Run(
    "info",
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => Info(
        parseResult.GetValue(infoRoot),
        parseResult.GetValue(infoName)!,
        parseResult.GetValue(infoProxy),
        IsOffline(parseResult),
        IsDiagnostics(parseResult),
        IsJson(parseResult),
        parseResult.GetValue(infoVersions))));

var doctor = new Command("doctor", "Check the lock, store, deployment, and partial downloads.\nExamples:\nsk doctor\nsk doctor --clean" + "\n" + "Docs: https://hoshi.srcz.one/seiko/troubleshooting/");
var doctorRoot = RootOption();
var doctorStore = StoreOption();
var doctorClean = new Option<bool>("--clean") { Description = "Delete all partial downloads." };
doctor.Add(doctorRoot);
doctor.Add(doctorStore);
doctor.Add(doctorClean);
doctor.SetAction(parseResult => Run(
    "doctor",
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => RunDoctor(
        "doctor",
        parseResult.GetValue(doctorRoot),
        parseResult.GetValue(doctorStore),
        parseResult.GetValue(doctorClean),
        IsJson(parseResult))));

// `check` is the read-only terminal summary of doctor; it never deletes .part.
var check = new Command("check", "Check the lock, store, deployment, and partial downloads.");
var checkRoot = RootOption();
var checkStore = StoreOption();
check.Add(checkRoot);
check.Add(checkStore);
check.SetAction(parseResult => Run(
    "check",
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => RunDoctor(
        "check",
        parseResult.GetValue(checkRoot),
        parseResult.GetValue(checkStore),
        clean: false,
        IsJson(parseResult))));

var why = new Command("why", "Explain why a sei is in the dependency graph.");
var whyRoot = RootOption();
var whyName = new Argument<string>("name") { Description = "Sei name." };
why.Add(whyName);
why.Add(whyRoot);
why.SetAction(parseResult => Run(
    "why",
    parseResult.GetValue(colorOption) ?? "auto",
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
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => Outdated(
        parseResult.GetValue(outdatedRoot),
        parseResult.GetValue(outdatedProxy),
        parseResult.GetValue(outdatedPre),
        IsOffline(parseResult),
        IsDiagnostics(parseResult),
        IsJson(parseResult))));

var audit = new Command("audit", "Check installed sei against advisories and retirement.\nExamples:\nsk audit\nsk audit --security-only --fail-on high" + "\n" + "Docs: https://hoshi.srcz.one/seiko/troubleshooting/");
var auditRoot = RootOption();
var auditProxy = ProxyOption();
var auditSecurity = new Option<bool>("--security-only")
{
    Description = "Only report advisories, not yank or deprecation.",
};
var auditFailOn = new Option<string?>("--fail-on")
{
    Description = "Exit 1 at this severity or worse: low, moderate, high, critical.",
};
audit.Add(auditRoot);
audit.Add(auditProxy);
audit.Add(auditSecurity);
audit.Add(auditFailOn);
audit.SetAction(parseResult => Run(
    "audit",
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => Audit(
        parseResult.GetValue(auditRoot),
        parseResult.GetValue(auditProxy),
        parseResult.GetValue(auditSecurity),
        parseResult.GetValue(auditFailOn),
        IsOffline(parseResult),
        IsJson(parseResult))));

var source = new Command("source", "Manage configured sources.");
var sourceList = new Command("list", "List configured sources.");
var sourceListRoot = RootOption();
sourceList.Add(sourceListRoot);
sourceList.SetAction(parseResult => Run(
    "source_list",
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => SourceListCommand(parseResult.GetValue(sourceListRoot), IsJson(parseResult))));

var sourceStatus = new Command("status", "Check source reachability, index, and catalog.");
var sourceStatusRoot = RootOption();
var sourceStatusProxy = ProxyOption();
var sourceStatusId = new Argument<string?>("id")
{
    Description = "Source ID (defaults to every source).",
    Arity = ArgumentArity.ZeroOrOne,
};
sourceStatus.Add(sourceStatusId);
sourceStatus.Add(sourceStatusRoot);
sourceStatus.Add(sourceStatusProxy);
sourceStatus.SetAction(parseResult => Run(
    "source_status",
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => SourceStatusCommand(
        parseResult.GetValue(sourceStatusRoot),
        parseResult.GetValue(sourceStatusId),
        parseResult.GetValue(sourceStatusProxy),
        IsOffline(parseResult),
        IsJson(parseResult))));
source.Add(sourceStatus);

var sourceAdd = new Command("add", "Add a source.");
var sourceAddRoot = RootOption();
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
sourceAdd.Add(sourceAddTokenHosts);
sourceAdd.SetAction(parseResult => Run(
    "source_add",
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => SourceAddCommand(
        parseResult.GetValue(sourceAddRoot),
        parseResult.GetValue(sourceAddId)!,
        parseResult.GetValue(sourceAddIndex)!,
        parseResult.GetValue(sourceAddTokenHosts),
        IsJson(parseResult))));

var sourceRemove = new Command("remove", "Remove a source.");
var sourceRemoveRoot = RootOption();
var sourceRemoveId = new Argument<string>("id") { Description = "Source id." };
sourceRemove.Add(sourceRemoveId);
sourceRemove.Add(sourceRemoveRoot);
sourceRemove.SetAction(parseResult => Run(
    "source_remove",
    parseResult.GetValue(colorOption) ?? "auto",
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
    parseResult.GetValue(colorOption) ?? "auto",
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
    parseResult.GetValue(colorOption) ?? "auto",
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
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => CacheStatusCommand(parseResult.GetValue(cacheStatusStore), IsJson(parseResult))));

var cacheVerify = new Command("verify", "Verify stored artifacts.");
var cacheVerifyStore = StoreOption();
cacheVerify.Add(cacheVerifyStore);
cacheVerify.SetAction(parseResult => Run(
    "cache_verify",
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => CacheVerifyCommand(parseResult.GetValue(cacheVerifyStore), IsJson(parseResult))));

var cacheClean = new Command("clean", "Remove artifacts not referenced by this install root.");
var cacheCleanRoot = RootOption();
var cacheCleanStore = StoreOption();
cacheClean.Add(cacheCleanRoot);
cacheClean.Add(cacheCleanStore);
cacheClean.SetAction(parseResult => Run(
    "cache_clean",
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => CacheCleanCommand(
        parseResult.GetValue(cacheCleanRoot),
        parseResult.GetValue(cacheCleanStore),
        IsJson(parseResult))));

cache.Add(cacheStatus);
cache.Add(cacheVerify);
cache.Add(cacheClean);

var index = new Command("index", "Inspect static index repositories.");
var indexVerify = new Command("verify", "Validate an index directory or a sei-index.json file.");
var indexVerifyPath = new Argument<string?>("path")
{
    Description = "Index directory or sei-index.json file (defaults to the current directory).",
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
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => IndexVerifyCommand(
        parseResult.GetValue(indexVerifyPath),
        parseResult.GetValue(indexVerifyArtifacts),
        parseResult.GetValue(indexVerifyStrict),
        parseResult.GetValue(indexVerifyProxy),
        IsJson(parseResult))));
index.Add(indexVerify);

var indexCatalog = new Command("catalog", "Generate catalog.json, or check it with --check.");
var indexCatalogPath = new Argument<string?>("path")
{
    Description = "Index directory (defaults to the current directory).",
    Arity = ArgumentArity.ZeroOrOne,
};
var indexCatalogCheck = new Option<bool>("--check")
{
    Description = "Only compare; exit 1 when the catalog is missing or stale.",
};
indexCatalog.Add(indexCatalogPath);
indexCatalog.Add(indexCatalogCheck);
indexCatalog.SetAction(parseResult => Run(
    "index_catalog",
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => IndexCatalogCommand(
        parseResult.GetValue(indexCatalogPath),
        parseResult.GetValue(indexCatalogCheck),
        IsJson(parseResult))));
index.Add(indexCatalog);

var completion = new Command("completion", "Print a shell completion script.");
var completionShell = new Argument<string>("shell")
{
    Description = "bash, zsh, fish, or powershell.",
};
completion.Add(completionShell);
completion.SetAction(parseResult => Run(
    "completion",
    parseResult.GetValue(colorOption) ?? "auto",
    IsJson(parseResult),
    () => Completion(parseResult.GetValue(completionShell)!, IsJson(parseResult))));

var help = new Command("help", "Show help.");
help.SetAction(_ => rootCommand.Parse(["--help"]).Invoke());

rootCommand.Add(init);
rootCommand.Add(sync);
rootCommand.Add(add);
rootCommand.Add(remove);
rootCommand.Add(update);
rootCommand.Add(why);
rootCommand.Add(outdated);
rootCommand.Add(audit);
rootCommand.Add(list);
rootCommand.Add(rollback);
rootCommand.Add(generation);
rootCommand.Add(search);
rootCommand.Add(info);
rootCommand.Add(doctor);
rootCommand.Add(check);
rootCommand.Add(source);
rootCommand.Add(config);
rootCommand.Add(cache);
rootCommand.Add(index);
rootCommand.Add(completion);
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

static bool IsOffline(ParseResult parseResult) => parseResult.GetValue<bool>("--offline");

static bool IsDiagnostics(ParseResult parseResult) => parseResult.GetValue<bool>("--diagnostics");

static int Run(string command, string color, bool json, Func<int> action)
{
    Color.Configure(json ? "never" : color);
    Loc.Configure(null);
    try
    {
        return action();
    }
    catch (SeikoException ex)
    {
        return Fail(
            command,
            json,
            ex.Code ?? "internal",
            ex.Message,
            ex.Code == "usage" ? 2 : 1,
            ex.Hint,
            ex.HintKey);
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

    var hint = (ex as SeikoException)?.Hint;
    return Fail(command, json, code, ex.Message, 1, hint);
}

static int Fail(
    string command,
    bool json,
    string code,
    string message,
    int exitCode,
    string? hint = null,
    string? hintKey = null)
{
    if (json)
    {
        WriteJson(
            CliJsonContext.Default.ErrorEnvelope,
            new ErrorEnvelope(1, command, new ErrorData(code, message, hint)));
    }
    else
    {
        Console.Error.WriteLine($"{Color.Error("sk: " + message)}");
        if (!string.IsNullOrWhiteSpace(hint))
        {
            Console.Error.WriteLine(Color.Dim("  " + Loc.Get("hint", Loc.Get(hintKey ?? "hint", hint))));
        }
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

static Option<bool> DryRunOption()
    => new("--dry-run") { Description = "Resolve and print the plan without downloading or writing." };

static Option<bool> AdoptOption()
    => new("--adopt") { Description = "Keep existing files and record them as adopted." };

static Option<bool> ForceOption()
    => new("--force") { Description = "Overwrite or delete conflicting existing files." };

static int Sync(
    string? rootOption,
    string? store,
    bool pre,
    bool frozen,
    bool locked,
    int jobs,
    string? proxy,
    bool latestInstallable,
    bool allowCritical,
    bool dryRun,
    bool adopt,
    bool force,
    bool offline,
    bool diagnostics,
    bool json)
{
    if (frozen && locked)
    {
        throw new SeikoException("--frozen and --locked are mutually exclusive", "usage");
    }

    var root = ResolveRoot(rootOption);
    var mode = frozen ? SyncMode.Frozen : locked ? SyncMode.Locked : SyncMode.Normal;
    var showProgress = !json && !Log.Quiet && ProgressDisplay.IsSupported();
    SyncResult result;
    if (showProgress)
    {
        using var display = new ProgressDisplay();
        result = new SeikoClient().Sync(
            root,
            store,
            pre,
            mode,
            jobs,
            proxy,
            display,
            Log.Debug,
            latestInstallable: latestInstallable,
            allowCritical: allowCritical,
            dryRun: dryRun,
            adopt: adopt,
            force: force,
            offline: offline);
    }
    else
    {
        result = new SeikoClient().Sync(
            root,
            store,
            pre,
            mode,
            jobs,
            proxy,
            log: Log.Debug,
            latestInstallable: latestInstallable,
            allowCritical: allowCritical,
            dryRun: dryRun,
            adopt: adopt,
            force: force,
            offline: offline);
    }

    if (dryRun)
    {
        return PrintPreview("sync", result.Preview!, json, diagnostics);
    }

    if (json)
    {
        WriteJson(
            CliJsonContext.Default.SyncEnvelope,
            new SyncEnvelope(1, "sync", new SyncData(
                result.SeiInstalled,
                result.FilesDeployed,
                mode.ToString(),
                result.Warnings.ToList(),
                diagnostics ? result.Diagnostics.ToList() : null,
                result.ExternalItems,
                result.ExternalHosts.ToList())));
        return 0;
    }

    PrintSyncResult(result, diagnostics);
    return 0;
}

static int PrintPreview(string command, PreviewResult preview, bool json, bool diagnostics)
{
    if (json)
    {
        WriteJson(
            CliJsonContext.Default.PreviewEnvelope,
            new PreviewEnvelope(1, command, new PreviewData(
                preview.Changes.ToList(),
                preview.Blocked.ToList(),
                preview.Warnings.ToList(),
                false,
                diagnostics ? preview.Diagnostics.ToList() : null,
                preview.ExternalItems,
                preview.ExternalBytes,
                preview.ExternalHosts.ToList())));
        return 0;
    }

    foreach (var change in preview.Changes)
    {
        var from = change.From is null ? "" : $" {change.From} ->";
        var to = change.To is null ? "" : $" {change.To}";
        var detail = change.Source is null
            ? ""
            : $"source={change.Source} artifact={change.Artifact}";
        Console.WriteLine($"{change.Action}\t{change.Name}{from}{to}\t{detail}".TrimEnd());
    }

    foreach (var warning in preview.Warnings)
    {
        Console.WriteLine($"warning\t{warning}");
    }

    foreach (var block in preview.Blocked)
    {
        Console.WriteLine($"blocked\t{block.Code}: {block.Message}");
    }

    if (diagnostics)
    {
        foreach (var diagnostic in preview.Diagnostics)
        {
            Console.WriteLine($"diagnostic\t{diagnostic.Name}\t{diagnostic.Kind}: {diagnostic.Message}");
        }
    }

    if (preview.ExternalItems > 0)
    {
        Console.WriteLine(
            $"external	{preview.ExternalItems} item(s), ~{preview.ExternalBytes} byte(s) from "
            + string.Join(", ", preview.ExternalHosts));
    }

    Console.WriteLine(Loc.Get("preview-done"));
    return 0;
}

static int Add(
    string? rootOption,
    IReadOnlyList<string> specifications,
    IReadOnlyList<string>? remove,
    string? store,
    bool pre,
    int jobs,
    string? proxy,
    bool latestInstallable,
    bool allowCritical,
    bool dryRun,
    bool adopt,
    bool force,
    bool offline,
    bool diagnostics,
    bool json)
    => RunMutation(
        "add",
        json,
        dryRun,
        diagnostics,
        (progress, log) => new SeikoClient().AddMany(
            ResolveRoot(rootOption),
            specifications,
            remove,
            store,
            pre,
            jobs,
            proxy,
            progress,
            log,
            latestInstallable: latestInstallable,
            allowCritical: allowCritical,
            dryRun: dryRun,
            adopt: adopt,
            force: force,
            offline: offline));

static int Remove(
    string? rootOption,
    string name,
    string? store,
    bool pre,
    int jobs,
    string? proxy,
    bool allowCritical,
    bool dryRun,
    bool purge,
    bool adopt,
    bool force,
    bool offline,
    bool diagnostics,
    bool json)
    => RunMutation(
        "remove",
        json,
        dryRun,
        diagnostics,
        (progress, log) => new SeikoClient().Remove(
            ResolveRoot(rootOption),
            name,
            store,
            pre,
            jobs,
            proxy,
            progress,
            log,
            allowCritical: allowCritical,
            dryRun: dryRun,
            purge: purge,
            adopt: adopt,
            force: force,
            offline: offline));

static int Update(
    string? rootOption,
    string? name,
    bool latest,
    string? store,
    bool pre,
    int jobs,
    string? proxy,
    bool latestInstallable,
    bool allowCritical,
    bool dryRun,
    bool adopt,
    bool force,
    bool offline,
    bool diagnostics,
    bool json)
    => RunMutation(
        "update",
        json,
        dryRun,
        diagnostics,
        (progress, log) => new SeikoClient().Update(
            ResolveRoot(rootOption),
            name,
            latest,
            store,
            pre,
            jobs,
            proxy,
            progress,
            log,
            latestInstallable: latestInstallable,
            allowCritical: allowCritical,
            dryRun: dryRun,
            adopt: adopt,
            force: force,
            offline: offline));

static int Why(string? rootOption, string name, bool json)
{
    var root = ResolveRoot(rootOption);
    var chains = new SeikoClient().WhyDetailed(root, name);
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
        Console.WriteLine(string.Join(
            " -> ",
            chain.Select(link => link.Kind == "direct" ? link.Name : $"{link.Name} ({link.Kind})")));
    }

    return 0;
}

static int Outdated(string? rootOption, string? proxy, bool pre, bool offline, bool diagnostics, bool json)
{
    var root = ResolveRoot(rootOption);
    var items = new SeikoClient().Outdated(root, proxy, pre, offline);
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

    Console.WriteLine("NAME	CURRENT	LATEST-IN-RANGE	LATEST	LATEST-INSTALLABLE");
    foreach (var item in items)
    {
        Console.WriteLine($"{item.Name}	{item.Current}	{item.LatestInRange ?? "-"}	{item.Latest}	{item.LatestInstallable ?? "-"}");
        foreach (var blocker in item.Blockers)
        {
            Console.WriteLine($"  blocked by {blocker.Relation} {blocker.Target} {blocker.Range ?? ""} ({blocker.Source})");
        }
    }

    return 0;
}

static int SourceListCommand(string? rootOption, bool json)
{
    var root = ResolveRoot(rootOption);
    var sources = new SeikoClient().ListSources(root);
    PrintSources("source_list", sources, json);
    return 0;
}

static int SourceAddCommand(
    string? rootOption,
    string id,
    string index,
    string[]? tokenHosts,
    bool json)
{
    var root = ResolveRoot(rootOption);
    var sources = new SeikoClient().SourceAdd(root, id, index, tokenHosts);
    PrintSources("source_add", sources, json);
    return 0;
}

static int SourceRemoveCommand(string? rootOption, string id, bool json)
{
    var root = ResolveRoot(rootOption);
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
    var root = ResolveRoot(rootOption);
    var value = new SeikoClient().ConfigGet(root, key);
    PrintConfig("config_get", key, value, json);
    return 0;
}

static int ConfigSetCommand(string? rootOption, string key, string value, bool json)
{
    var root = ResolveRoot(rootOption);
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
    Console.WriteLine($"staging: {status.StagingItems}");
    Console.WriteLine($"staging bytes: {status.StagingBytes}");
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
        Console.WriteLine(Loc.Get("no-problems"));
    }

    return hasError ? 1 : 0;
}

static int CacheCleanCommand(string? rootOption, string? store, bool json)
{
    var root = ResolveRoot(rootOption);
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
    bool dryRun,
    bool diagnostics,
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

    if (dryRun)
    {
        return PrintPreview(command, result.Preview!, json, diagnostics);
    }

    if (json)
    {
        WriteJson(
            CliJsonContext.Default.SyncEnvelope,
            new SyncEnvelope(1, command, new SyncData(
                result.SeiInstalled,
                result.FilesDeployed,
                SyncMode.Normal.ToString(),
                result.Warnings.ToList(),
                diagnostics ? result.Diagnostics.ToList() : null,
                result.ExternalItems,
                result.ExternalHosts.ToList())));
    }
    else
    {
        PrintSyncResult(result, diagnostics);
    }

    return 0;
}

static void PrintSyncResult(SyncResult result, bool diagnostics)
{
    foreach (var warning in result.Warnings)
    {
        Log.Warning(Color.Warning(warning));
    }

    Console.WriteLine(Loc.Get("sync-done", result.SeiInstalled, result.FilesDeployed));
    if (result.ExternalItems > 0)
    {
        Console.WriteLine(
            Loc.Get("external", result.ExternalItems, string.Join(", ", result.ExternalHosts)));
    }

    foreach (var sei in result.Lock.Sei)
    {
        Console.WriteLine($"  {sei.Name} {sei.Version}{(sei.Direct ? " (direct)" : "")}");
    }

    if (diagnostics)
    {
        foreach (var diagnostic in result.Diagnostics)
        {
            Console.WriteLine($"diagnostic	{diagnostic.Name}	{diagnostic.Kind}: {diagnostic.Message}");
        }
    }
}

static int List(string? rootOption, bool json)
{
    var root = ResolveRoot(rootOption);
    var lockFile = new SeikoClient().ReadLock(root);
    var items = new List<ListItem>();
    if (lockFile is not null)
    {
        foreach (var sei in lockFile.Sei)
        {
            var selectors = sei.Artifact.Rid is null
                ? null
                : new Dictionary<string, string>(StringComparer.Ordinal) { ["rid"] = sei.Artifact.Rid };
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
        var selectors = sei.Artifact.Rid is null ? "-" : $"rid={sei.Artifact.Rid}";
        Console.WriteLine($"{sei.Name}\t{sei.Version}\t{(sei.Direct ? "yes" : "no")}\t{sei.Source}\t{selectors}");
    }

    return 0;
}

static int Search(
    string? rootOption,
    string[] terms,
    string? proxy,
    bool offline,
    bool diagnostics,
    bool json,
    string? tag,
    string? license,
    int? limit)
{
    var root = ResolveRoot(rootOption);
    var client = new SeikoClient();
    // Every term must match somewhere: several terms narrow the result, they
    // do not widen it.
    List<SeiSummary> results = [];
    var first = true;
    foreach (var term in terms)
    {
        var matches = client.Search(root, term, proxy, offline, null, tag, license, null);
        results = first
            ? matches.ToList()
            : results.Where(item => matches.Any(match => match.Name == item.Name)).ToList();
        first = false;
    }

    if (limit is > 0)
    {
        results = results.Take(limit.Value).ToList();
    }

    if (json)
    {
        WriteJson(CliJsonContext.Default.SearchEnvelope, new SearchEnvelope(1, "search", results.ToList()));
        return 0;
    }

    Console.WriteLine("NAME\tVERSION\tSOURCE\tLICENSE\tDESCRIPTION");
    foreach (var result in results)
    {
        var flags = result.Deprecated is null ? "" : "\tdeprecated";
        Console.WriteLine(
            $"{result.Name}\t{result.Version}\t{result.Source}\t{result.License ?? ""}\t{result.Description ?? ""}{flags}");
    }

    return 0;
}

static int Info(
    string? rootOption,
    string name,
    string? proxy,
    bool offline,
    bool diagnostics,
    bool json,
    bool allVersions)
{
    var root = ResolveRoot(rootOption);
    var info = new SeikoClient().Info(root, name, proxy, offline);
    if (json)
    {
        var data = new InfoData(
            info.Name,
            info.Source,
            info.Unlisted,
            info.Deprecated,
            info.Versions
                .Select(version => new InfoVersion(
                    version.Version,
                    version.Yanked,
                    version.Deprecated,
                    version.Advisory,
                    version.ArtifactCount,
                    version.Dependencies.ToList(),
                    version.Prerequisites.ToList(),
                    version.OptionalPeers.ToList(),
                    version.Conflicts.ToList(),
                    version.Artifacts
                        .Select(artifact => new InfoArtifact(
                            artifact.Rid,
                            artifact.Dependencies.ToList(),
                            artifact.Prerequisites.ToList(),
                            artifact.OptionalPeers.ToList(),
                            artifact.Conflicts.ToList(),
                            artifact.ExternalCount,
                            artifact.ExternalHosts.ToList()))
                        .ToList(),
                    version.SourceRetired))
                .ToList(),
            info.Project,
            info.UpstreamHost,
            info.MaintainedHere);
        WriteJson(CliJsonContext.Default.InfoEnvelope, new InfoEnvelope(1, "info", data));
        return 0;
    }

    var packageFlags = info.Unlisted ? " unlisted" : "";
    if (!string.IsNullOrWhiteSpace(info.Deprecated))
    {
        packageFlags += $" deprecated: {info.Deprecated}";
    }

    Console.WriteLine($"{info.Name} ({info.Source}){packageFlags}");
    if (info.Project is { } project)
    {
        if (!string.IsNullOrWhiteSpace(project.Description))
        {
            Console.WriteLine($"  {project.Description}");
        }

        Console.WriteLine($"  authors: {string.Join(", ", project.Authors)}  license: {project.License}");
        if (project.Tags is { Count: > 0 })
        {
            Console.WriteLine($"  tags: {string.Join(", ", project.Tags)}");
        }

        if (!string.IsNullOrWhiteSpace(project.ProjectUrl))
        {
            Console.WriteLine($"  project: {project.ProjectUrl}");
        }

        if (!string.IsNullOrWhiteSpace(project.RepositoryUrl))
        {
            Console.WriteLine($"  repository: {project.RepositoryUrl}");
        }
    }

    if (info.UpstreamHost is not null)
    {
        Console.WriteLine($"  maintained upstream: {info.UpstreamHost}");
    }

    // Long version histories are noise by default; notable versions and the
    // installed one stay visible, --versions expands the rest.
    var installed = InstalledVersion(root, info.Name);
    var shown = allVersions
        ? info.Versions
        : info.Versions
            .Where(version => version == info.Versions[0]
                || version.Yanked
                || version.Advisory is not null
                || string.Equals(version.Version, installed, StringComparison.Ordinal))
            .ToList();
    if (!allVersions && shown.Count < info.Versions.Count)
    {
        Console.WriteLine($"  {info.Versions.Count - shown.Count} more version(s); use --versions");
    }

    foreach (var version in shown)
    {
        var flags = version.Yanked ? " yanked" : "";
        if (version.SourceRetired)
        {
            flags += " (by source)";
        }

        if (string.Equals(version.Version, installed, StringComparison.Ordinal))
        {
            flags += " installed";
        }

        if (!string.IsNullOrWhiteSpace(version.Deprecated))
        {
            flags += $" deprecated: {version.Deprecated}";
        }

        if (version.Advisory is { } advisory)
        {
            flags += $" advisory: {advisory.Severity}";
            if (!string.IsNullOrWhiteSpace(advisory.Url))
            {
                flags += $" ({advisory.Url})";
            }
        }

        Console.WriteLine($"  {version.Version}{flags}  deps={version.Dependencies.Count} artifacts={version.ArtifactCount}");
        WriteRelations(version.Dependencies, "dependency");
        WriteRelations(version.Prerequisites, "prerequisite");
        WriteRelations(version.OptionalPeers, "optional peer");
        WriteRelations(version.Conflicts, "conflict");
        foreach (var artifact in version.Artifacts)
        {
            Console.WriteLine($"    artifact rid={artifact.Rid ?? "*"}");
            if (artifact.ExternalCount > 0)
            {
                Console.WriteLine(
                    $"      external: {artifact.ExternalCount} item(s) from {string.Join(", ", artifact.ExternalHosts)}");
            }

            WriteRelations(artifact.Dependencies, "dependency", 6);
            WriteRelations(artifact.Prerequisites, "prerequisite", 6);
            WriteRelations(artifact.OptionalPeers, "optional peer", 6);
            WriteRelations(artifact.Conflicts, "conflict", 6);
        }
    }

    return 0;
}

static string? InstalledVersion(string root, string name)
{
    var lockFile = LockFile.Read(Path.Combine(root, "seiko.lock.json"));
    return lockFile?.Sei.FirstOrDefault(sei => sei.Name == name)?.Version;
}

static void WriteRelations(IReadOnlyList<SeiDependency> relations, string kind, int indent = 4)
{
    if (relations.Count == 0)
    {
        return;
    }

    var pad = new string(' ', indent);
    foreach (var relation in relations)
    {
        Console.WriteLine($"{pad}{kind}: {relation.Name} {relation.Version ?? "*"}");
    }
}

static int SourceStatusCommand(
    string? rootOption,
    string? id,
    string? proxy,
    bool offline,
    bool json)
{
    var root = ResolveRoot(rootOption);
    var statuses = new SeikoClient().SourceStatus(root, id, proxy, offline);
    var failed = statuses.Any(status => !status.Reachable);
    if (json)
    {
        WriteJson(
            CliJsonContext.Default.SourceStatusEnvelope,
            new SourceStatusEnvelope(1, "source_status", statuses.ToList()));
        return failed ? 1 : 0;
    }

    foreach (var status in statuses)
    {
        var catalog = status.CatalogPresent ? "catalog" : "no catalog (warning)";
        var count = status.SeiCount is null ? "?" : status.SeiCount.Value.ToString();
        Console.WriteLine($"{status.Id}	{(status.Reachable ? "reachable" : "unreachable")}	{count} sei	{catalog}");
        foreach (var problem in status.Problems)
        {
            Console.WriteLine($"  {problem}");
        }
    }

    Console.WriteLine(failed
        ? "a source is unreachable; check the proxy or token, or use --offline"
        : "all sources answered; source order is unchanged");
    return failed ? 1 : 0;
}

static int Audit(
    string? rootOption,
    string? proxy,
    bool securityOnly,
    string? failOn,
    bool offline,
    bool json)
{
    var threshold = 0;
    if (failOn is not null)
    {
        threshold = failOn.ToLowerInvariant() switch
        {
            "low" => 1,
            "moderate" => 2,
            "high" => 3,
            "critical" => 4,
            _ => throw new SeikoException(
                "--fail-on requires low, moderate, high, or critical",
                "usage"),
        };
    }

    var root = ResolveRoot(rootOption);
    var result = new SeikoClient().Audit(root, proxy, offline, securityOnly: securityOnly);

    static int Rank(string? severity) => severity?.ToLowerInvariant() switch
    {
        "critical" => 4,
        "high" => 3,
        "moderate" => 2,
        "low" => 1,
        _ => 0,
    };

    var failed = threshold > 0 && result.Findings.Any(finding => Rank(finding.Severity) >= threshold);
    // Unknown is not safe: a source that could not answer fails the command.
    failed |= result.Unknown.Count > 0;

    if (json)
    {
        WriteJson(
            CliJsonContext.Default.AuditEnvelope,
            new AuditEnvelope(1, "audit", new AuditData(
                result.Findings.ToList(),
                result.Unknown.ToList())));
        return failed ? 1 : 0;
    }

    foreach (var finding in result.Findings)
    {
        var detail = finding.Url is null ? "" : $" ({finding.Url})";
        var fix = finding.Fix is null ? "no clean version found" : $"fix: {finding.Fix}";
        var severity = finding.Severity is null ? "" : $"[{finding.Severity}] ";
        Console.WriteLine($"{finding.Name} {finding.Version}  {severity}{finding.Kind}{detail}  {fix}");
    }

    foreach (var name in result.Unknown)
    {
        Console.WriteLine(Loc.Get("unknown", name));
    }

    Console.WriteLine(
        $"{result.Findings.Count} finding(s), {result.Unknown.Count} unknown; "
        + "`sk update` applies fixes");
    return failed ? 1 : 0;
}

static int Rollback(
    string? rootOption,
    string? store,
    int? toGeneration,
    string? proxy,
    bool offline,
    bool diagnostics,
    bool json,
    bool dryRun)
{
    if (toGeneration is < 1)
    {
        throw new SeikoException("--to requires a positive generation number", "usage");
    }

    var root = ResolveRoot(rootOption);
    if (dryRun)
    {
        // Preview never fetches; --offline is implied and accepted.
        var preview = new SeikoClient().PreviewRollback(root, toGeneration, store);
        if (json)
        {
            WriteJson(
                CliJsonContext.Default.RollbackPreviewEnvelope,
                new RollbackPreviewEnvelope(1, "rollback", preview));
            return 0;
        }

        Console.WriteLine($"rollback preview: generation {preview.FromGeneration} -> {preview.ToGeneration}");
        WritePreviewList(preview.ConfigChanges);
        WritePreviewList(preview.LockChanges);
        WritePreviewList(preview.Added.Select(path => $"+ add {path}"));
        WritePreviewList(preview.Removed.Select(path => $"- remove {path}"));
        WritePreviewList(preview.Replaced.Select(path => $"~ replace {path}"));
        WritePreviewList(preview.Adopted.Select(path => $"= adopted {path}"));
        WritePreviewList(preview.MissingStore.Select(path => $"! store entry missing, download needed: {path}"));
        Console.WriteLine(Loc.Get("preview-done"));
        return 0;
    }

    var result = new SeikoClient().Rollback(root, store, toGeneration, proxy, offline);
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

static void WritePreviewList(IEnumerable<string> lines)
{
    foreach (var line in lines)
    {
        Console.WriteLine($"  {line}");
    }
}

static int Generations(string? rootOption, string? store, bool json)
{
    var root = ResolveRoot(rootOption);
    var generations = new SeikoClient().Generations(root, store);
    if (json)
    {
        WriteJson(
            CliJsonContext.Default.GenerationEnvelope,
            new GenerationEnvelope(1, "generation_list", new GenerationData(generations.ToList())));
        return 0;
    }

    Console.WriteLine("GENERATION\tCREATED\tCURRENT\tSEI\tFILES\tROLLBACKABLE");
    foreach (var generation in generations)
    {
        Console.WriteLine(
            $"{generation.Generation}\t{generation.CreatedAt?.UtcDateTime.ToString("u") ?? "unknown"}\t"
            + $"{(generation.Current ? "yes" : "")}\t{generation.SeiCount}\t{generation.FileCount}\t"
            + $"{(generation.Valid ? "yes" : "no")}");
    }

    return 0;
}

static int Init(string? rootOption, bool json)
{
    var path = InitTemplate.Write(Path.GetFullPath(rootOption ?? Directory.GetCurrentDirectory()));
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
    var result = IndexVerifier.VerifyPath(directory, checkArtifacts, proxy, Log.Debug);
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

static int IndexCatalogCommand(string? pathOption, bool check, bool json)
{
    var directory = Path.GetFullPath(pathOption ?? Directory.GetCurrentDirectory());
    var path = Path.Combine(directory, CatalogFile.FileName);
    var expected = CatalogFile.Build(CatalogFile.Load(directory));
    var stale = true;
    if (File.Exists(path))
    {
        var existing = CatalogFile.TryRead(path);
        stale = existing is null || existing.Sei.Count != expected.Sei.Count
            || JsonSerializer.Serialize(existing, SeikoJsonContext.Default.SeiCatalog)
                != JsonSerializer.Serialize(expected, SeikoJsonContext.Default.SeiCatalog);
    }

    var wrote = false;
    if (!check || stale)
    {
        if (!check)
        {
            CatalogFile.Write(path, expected);
            wrote = true;
        }
    }

    if (json)
    {
        WriteJson(
            CliJsonContext.Default.IndexCatalogEnvelope,
            new IndexCatalogEnvelope(1, "index_catalog", new IndexCatalogData(
                expected.Sei.Count,
                wrote,
                stale)));
        return check && stale ? 1 : 0;
    }

    if (check)
    {
        Console.WriteLine(stale
            ? "catalog.json is missing or stale"
            : "catalog.json is up to date");
        return stale ? 1 : 0;
    }

    Console.WriteLine($"wrote catalog.json with {expected.Sei.Count} sei");
    return 0;
}

static int RunDoctor(string command, string? rootOption, string? store, bool clean, bool json)
{
    var root = ResolveRoot(rootOption);
    var result = new Doctor().Run(root, store, clean);
    var hasError = result.Findings.Any(finding => finding.Severity == "error");
    if (json)
    {
        WriteJson(CliJsonContext.Default.DoctorEnvelope, new DoctorEnvelope(1, command, result));
        return hasError ? 1 : 0;
    }

    foreach (var finding in result.Findings)
    {
        var path = finding.Path is null ? "" : $" ({finding.Path})";
        var line = $"{finding.Severity} {finding.Code}: {finding.Message}{path}";
        Console.WriteLine(finding.Severity == "error" ? Color.Error(line) : Color.Warning(line));
        if (finding.Hint is not null)
        {
            Console.WriteLine("  " + Loc.Get("hint", Loc.Get(finding.HintKey ?? "hint", finding.Hint!)));
        }
    }

    if (result.CleanCount > 0)
    {
        Console.WriteLine($"cleaned {result.CleanCount} partial item(s)");
    }

    if (result.Findings.Count == 0)
    {
        Console.WriteLine(Loc.Get("no-problems"));
    }

    return hasError ? 1 : 0;
}

static int Completion(string? shell, bool json)
{
    var name = Path.GetFileName(shell ?? "");
    var script = name switch
    {
        "bash" => CompletionScript("bash"),
        "zsh" => CompletionScript("zsh"),
        "fish" => CompletionScript("fish"),
        "powershell" or "pwsh" => CompletionScript("powershell"),
        _ => throw new SeikoException(
            "completion requires bash, zsh, fish, or powershell",
            "usage"),
    };
    if (json)
    {
        WriteJson(CliJsonContext.Default.InitEnvelope, new InitEnvelope(1, "completion", new InitData(script)));
        return 0;
    }

    Console.Write(script);
    return 0;
}

static string CompletionScript(string shell)
{
    const string program = "sk";
    return shell switch
    {
        "bash" => $$"""
_{{program}}_complete() {
    local word="${COMP_WORDS[COMP_CWORD]:-}"
    local out
    out=$({{program}} "[suggest]" "$word" 2>/dev/null | tr '
' ' ')
    COMPREPLY=( $(compgen -W "$out" -- "$word") )
}
complete -F _{{program}}_complete {{program}}
""",
        "zsh" => $$"""
#compdef {{program}}
_{{program}}_complete() {
    local -a out
    out=($({{program}} "[suggest]" "${words[CURRENT]:-}" 2>/dev/null))
    compadd -a out
}
compdef _{{program}}_complete {{program}}
""",
        "fish" => $$"""
function __{{program}}_complete
    set -l word (commandline -ct)
    {{program}} "[suggest]" "$word" 2>/dev/null
end
complete -c {{program}} -f -a '(__{{program}}_complete)'
""",
        _ => $$"""
Register-ArgumentCompleter -Native -CommandName {{program}} -ScriptBlock {
    param($wordToComplete)
    {{program}} "[suggest]" "$wordToComplete" 2>/dev/null | ForEach-Object {
        [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
    }
}
""",
    };
}

static string ResolveRoot(string? rootOption)
{
    var root = rootOption is null ? FindRoot() : Path.GetFullPath(rootOption);
    Log.Root(root);
    return root;
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
