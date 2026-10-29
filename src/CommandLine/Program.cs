using System.CommandLine;
using Seiko;

var rootCommand = new RootCommand("sk - Seiko general-purpose package manager (sk is short for Seiko)");

var init = new Command("init", "Write a seiko.conf template.");
var initRoot = RootOption();
init.Add(initRoot);
init.SetAction(parseResult => Run(() => Init(parseResult.GetValue(initRoot))));

var sync = new Command("sync", "Resolve sei and deploy files into the install root.");
var syncRoot = RootOption();
var syncStore = StoreOption();
var syncPre = new Option<bool>("--pre") { Description = "Allow prerelease versions." };
var syncFrozen = new Option<bool>("--frozen") { Description = "Replay seiko.lock.json without resolving." };
var syncLocked = new Option<bool>("--locked") { Description = "Fail if the lock is out of date." };
sync.Add(syncRoot);
sync.Add(syncStore);
sync.Add(syncPre);
sync.Add(syncFrozen);
sync.Add(syncLocked);
sync.SetAction(parseResult => Run(() => Sync(
    parseResult.GetValue(syncRoot),
    parseResult.GetValue(syncStore),
    parseResult.GetValue(syncPre),
    parseResult.GetValue(syncFrozen),
    parseResult.GetValue(syncLocked))));

var list = new Command("list", "List installed sei from the lock.");
var listRoot = RootOption();
list.Add(listRoot);
list.SetAction(parseResult => Run(() => List(parseResult.GetValue(listRoot))));

var rollback = new Command("rollback", "Restore a previous generation.");
var rollbackRoot = RootOption();
var rollbackStore = StoreOption();
var rollbackTo = new Option<int?>("--to") { Description = "Generation number to roll back to." };
rollback.Add(rollbackRoot);
rollback.Add(rollbackStore);
rollback.Add(rollbackTo);
rollback.SetAction(parseResult => Run(() => Rollback(
    parseResult.GetValue(rollbackRoot),
    parseResult.GetValue(rollbackStore),
    parseResult.GetValue(rollbackTo))));

var search = new Command("search", "Search sei names in the configured indexes.");
var searchRoot = RootOption();
var searchTerm = new Argument<string>("term") { Description = "Name fragment to search for." };
search.Add(searchTerm);
search.Add(searchRoot);
search.SetAction(parseResult => Run(() => Search(
    parseResult.GetValue(searchRoot),
    parseResult.GetValue(searchTerm)!)));

var info = new Command("info", "Show versions, dependencies, and artifact counts.");
var infoRoot = RootOption();
var infoName = new Argument<string>("name") { Description = "Sei name." };
info.Add(infoName);
info.Add(infoRoot);
info.SetAction(parseResult => Run(() => Info(
    parseResult.GetValue(infoRoot),
    parseResult.GetValue(infoName)!)));

var help = new Command("help", "Show help.");
help.SetAction(_ => rootCommand.Parse(["--help"]).Invoke());

rootCommand.Add(init);
rootCommand.Add(sync);
rootCommand.Add(list);
rootCommand.Add(rollback);
rootCommand.Add(search);
rootCommand.Add(info);
rootCommand.Add(help);
rootCommand.SetAction(_ => rootCommand.Parse(["--help"]).Invoke());

var parseResult = rootCommand.Parse(args);
if (parseResult.Errors.Count > 0)
{
    foreach (var error in parseResult.Errors)
    {
        Console.Error.WriteLine($"sk: {error.Message}");
    }

    return 2;
}

return parseResult.Invoke();

static int Run(Func<int> action)
{
    try
    {
        return action();
    }
    catch (SeikoException ex)
    {
        Console.Error.WriteLine($"sk: {ex.Message}");
        return 1;
    }
}

static Option<string?> RootOption()
    => new("--root") { Description = "Install root (defaults to the nearest seiko.conf)." };

static Option<string?> StoreOption()
    => new("--store") { Description = "Content-addressed store directory." };

static int Sync(string? rootOption, string? store, bool pre, bool frozen, bool locked)
{
    if (frozen && locked)
    {
        throw new SeikoException("--frozen and --locked are mutually exclusive");
    }

    var root = rootOption ?? FindRoot();
    var mode = frozen ? SyncMode.Frozen : locked ? SyncMode.Locked : SyncMode.Normal;
    var result = new SeikoClient().Sync(root, store, pre, mode);
    Console.WriteLine($"resolved {result.SeiInstalled} sei, deployed {result.FilesDeployed} file(s)");
    foreach (var sei in result.Lock.Sei)
    {
        Console.WriteLine($"  {sei.Name} {sei.Version}{(sei.Direct ? " (direct)" : "")}");
    }

    return 0;
}

static int List(string? rootOption)
{
    var root = rootOption ?? FindRoot();
    var lockFile = new SeikoClient().ReadLock(root);
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

static int Search(string? rootOption, string term)
{
    var root = rootOption ?? FindRoot();
    var results = new SeikoClient().Search(root, term);
    Console.WriteLine("NAME\tVERSION\tSOURCE");
    foreach (var result in results)
    {
        Console.WriteLine($"{result.Name}\t{result.Version}\t{result.Source}");
    }

    return 0;
}

static int Info(string? rootOption, string name)
{
    var root = rootOption ?? FindRoot();
    var info = new SeikoClient().Info(root, name);
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

static int Rollback(string? rootOption, string? store, int? toGeneration)
{
    if (toGeneration is < 1)
    {
        throw new SeikoException("--to requires a positive generation number");
    }

    var root = rootOption ?? FindRoot();
    var result = new SeikoClient().Rollback(root, store, toGeneration);
    Console.WriteLine($"rolled back to generation {result.Generation}: {result.SeiInstalled} sei, {result.FilesDeployed} file(s)");
    return 0;
}

static int Init(string? rootOption)
{
    var root = Path.GetFullPath(rootOption ?? Directory.GetCurrentDirectory());
    Directory.CreateDirectory(root);
    var path = Path.Combine(root, "seiko.conf");
    if (File.Exists(path))
    {
        Console.Error.WriteLine($"sk: {path} already exists");
        return 1;
    }

    File.WriteAllText(path, """
# Seiko install root
# Add a source, then run `sk sync`.
sources = []

# rid = "win-x64"
sei = []
components = []
""" + "\n");
    Console.WriteLine($"created {path}");
    return 0;
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
