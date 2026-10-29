using Seiko.Core;

var command = args.Length == 0 ? "help" : args[0];
var options = CliOptions.Parse(args.Skip(1).ToArray());

try
{
    return command switch
    {
        "sync" => Sync(options),
        "list" => List(options),
        "rollback" => Rollback(options),
        "init" => Init(options),
        "help" or "--help" or "-h" => Help(),
        _ => Unknown(command),
    };
}
catch (SeikoException ex)
{
    Console.Error.WriteLine($"sk: {ex.Message}");
    return 1;
}

static int Sync(CliOptions options)
{
    var root = options.Root ?? FindRoot();
    var result = new SeikoEngine().Sync(root, options.Store, options.Pre, options.Mode);
    Console.WriteLine($"resolved {result.PackagesInstalled} package(s), projected {result.FilesProjected} file(s)");
    foreach (var package in result.Lock.Packages)
    {
        Console.WriteLine($"  {package.Name} {package.Version}{(package.Direct ? " (direct)" : "")}");
    }

    return 0;
}

static int List(CliOptions options)
{
    var root = options.Root ?? FindRoot();
    var lockFile = new SeikoEngine().ReadLock(root);
    if (lockFile is null)
    {
        Console.WriteLine("no lock; run `sk sync`");
        return 0;
    }

    Console.WriteLine("NAME\tVERSION\tDIRECT\tSOURCE\tSELECTORS");
    foreach (var package in lockFile.Packages)
    {
        var selectors = package.Artifact.Selectors is null
            ? "-"
            : string.Join(",", package.Artifact.Selectors.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
        Console.WriteLine($"{package.Name}\t{package.Version}\t{(package.Direct ? "yes" : "no")}\t{package.Source}\t{selectors}");
    }

    return 0;
}

static int Rollback(CliOptions options)
{
    var root = options.Root ?? FindRoot();
    var result = new SeikoEngine().Rollback(root, options.Store, options.ToGeneration);
    Console.WriteLine($"rolled back to generation {result.Generation}: {result.PackagesInstalled} package(s), {result.FilesProjected} file(s)");
    return 0;
}

static int Init(CliOptions options)
{
    var root = Path.GetFullPath(options.Root ?? Directory.GetCurrentDirectory());
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
packages = []
components = []
""" + "\n");
    Console.WriteLine($"created {path}");
    return 0;
}

static int Help()
{
    Console.WriteLine("""
sk - install-side package manager

usage:
  sk init [--root <dir>]
  sk sync [--root <dir>] [--store <dir>] [--pre] [--frozen|--locked]
  sk list [--root <dir>]
  sk rollback [--root <dir>] [--store <dir>] [--to <n>]

Seiko reads <root>/seiko.conf, resolves sei packages from the configured static
index, verifies artifacts, projects files into <root>, and writes seiko.lock.json.
""");
    return 0;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"sk: unknown command '{command}' (try `sk help`)");
    return 2;
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

internal sealed class CliOptions
{
    public string? Root { get; private set; }
    public string? Store { get; private set; }
    public bool Pre { get; private set; }
    public SyncMode Mode { get; private set; } = SyncMode.Normal;
    public int? ToGeneration { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root":
                    options.Root = Next(args, ref i, "--root");
                    break;
                case "--store":
                    options.Store = Next(args, ref i, "--store");
                    break;
                case "--pre":
                    options.Pre = true;
                    break;
                case "--frozen":
                    if (options.Mode == SyncMode.Locked)
                    {
                        throw new SeikoException("--frozen and --locked are mutually exclusive");
                    }

                    options.Mode = SyncMode.Frozen;
                    break;
                case "--locked":
                    if (options.Mode == SyncMode.Frozen)
                    {
                        throw new SeikoException("--frozen and --locked are mutually exclusive");
                    }

                    options.Mode = SyncMode.Locked;
                    break;
                case "--to":
                    var value = Next(args, ref i, "--to");
                    if (!int.TryParse(value, out var generation) || generation < 1)
                    {
                        throw new SeikoException("--to requires a positive generation number");
                    }

                    options.ToGeneration = generation;
                    break;
                default:
                    throw new SeikoException($"unknown option '{args[i]}'");
            }
        }

        return options;
    }

    private static string Next(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new SeikoException($"{option} requires a value");
        }

        return args[++index];
    }
}
