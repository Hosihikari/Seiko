using Seiko;
using Seiko.McpServer;

var root = Directory.GetCurrentDirectory();
var allowMutations = false;
var allowedRoots = new List<string>();
var allowedStores = new List<string>();
string? auditLog = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--root":
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine("seiko-mcp: --root requires a value");
                return 2;
            }

            root = args[++i];
            break;
        case "--allowed-root":
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine("seiko-mcp: --allowed-root requires a value");
                return 2;
            }

            allowedRoots.Add(args[++i]);
            break;
        case "--allowed-store":
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine("seiko-mcp: --allowed-store requires a value");
                return 2;
            }

            allowedStores.Add(args[++i]);
            break;
        case "--allow-mutations":
            allowMutations = true;
            break;
        case "--audit-log":
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine("seiko-mcp: --audit-log requires a value");
                return 2;
            }

            auditLog = args[++i];
            break;
        case "-h":
        case "--help":
            PrintHelp();
            return 0;
        case "-V":
        case "--version":
            Console.WriteLine(SeikoMcpServer.Version);
            return 0;
        default:
            Console.Error.WriteLine($"seiko-mcp: unknown option '{args[i]}'");
            return 2;
    }
}

SeikoMcpServer server;
try
{
    server = new SeikoMcpServer(root, allowMutations, allowedRoots, allowedStores, auditLog: auditLog);
}
catch (SeikoException ex)
{
    Console.Error.WriteLine($"seiko-mcp: {ex.Message}");
    return 2;
}

while (Console.ReadLine() is { } line)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    var response = server.Handle(line);
    if (response is not null)
    {
        Console.WriteLine(response);
        Console.Out.Flush();
    }
}

return 0;

static void PrintHelp()
{
    Console.WriteLine("""
        seiko-mcp - Seiko MCP server over stdio

        Usage: seiko-mcp [--root <path>] [--allow-mutations]

        Options:
          --root <path>          Install root to operate on (default: current directory)
          --allow-mutations      Register mutating tools; each mutation still requires an explicit root
          --allowed-root <path>  Restrict mutation roots to this directory (repeatable)
          --allowed-store <path> Restrict mutation stores to this directory (repeatable)
          --audit-log <path>     Append one JSON line per tool call (no credentials)
          -h, --help         Show this help
          -V, --version      Show version information
        """);
}
