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
        default:
            Console.Error.WriteLine($"seiko-mcp: unknown option '{args[i]}'");
            return 2;
    }
}

var server = new McpServer(root, allowMutations);
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
