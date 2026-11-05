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
        // Human text is localised; tests that assert English substrings must
        // not depend on the machine's UI language (ADR-0034). Locale tests
        // override LANG through the environment argument below.
        start.Environment["LANG"] = "en";
        start.Environment.Remove("LC_ALL");
        start.Environment.Remove("LC_MESSAGES");
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
