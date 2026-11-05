using System.Diagnostics;

namespace Seiko.Tests;

// The CLI lives in its own project output; the test project only references
// the library, so process-level tests resolve the binary by walking up to the
// repo root.
internal static class Cli
{
    private static string? Path()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = System.IO.Path.Combine(
                directory.FullName,
                "src",
                "CommandLine",
                "bin",
                "Debug",
                "net10.0",
                OperatingSystem.IsWindows() ? "sk.exe" : "sk");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    public static (string Stdout, string Stderr, int ExitCode) Run(
        IEnumerable<string> args,
        IDictionary<string, string>? environment = null)
    {
        var path = Path();
        if (path is null)
        {
            return ("", "", 0);
        }

        var start = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        start.Environment["DOTNET_NOLOGO"] = "1";
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment.Remove(key);
            start.Environment[key] = value;
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (stdout, stderr, process.ExitCode);
    }
}
