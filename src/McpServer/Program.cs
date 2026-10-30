using Seiko.McpServer;

var root = Directory.GetCurrentDirectory();
var allowMutations = false;

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
        case "--allow-mutations":
            allowMutations = true;
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

var server = new SeikoMcpServer(root, allowMutations);
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
          --root <path>      Install root to operate on (default: current directory)
          --allow-mutations  Register mutating tools; each mutation still requires an explicit root
          -h, --help         Show this help
          -V, --version      Show version information
        """);
}
