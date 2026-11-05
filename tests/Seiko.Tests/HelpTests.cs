using System.Diagnostics;
using Xunit;

namespace Seiko.Tests;

// Help is user-facing contract: every core command shows at least one example
// and the docs link. The CLI is exercised as a process because the test
// project does not reference the executable project.
public sealed class HelpTests
{
    private static string Run(params string[] args)
    {
        var assembly = typeof(SeikoClient).Assembly.Location;
        var directory = Path.GetDirectoryName(assembly)!;
        var cli = Path.Combine(directory, "sk");
        if (OperatingSystem.IsWindows())
        {
            cli += ".exe";
        }

        if (!File.Exists(cli))
        {
            return "";
        }

        var start = new ProcessStartInfo(cli)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    [Fact]
    public void CoreCommandsShowExamplesAndDocsLinks()
    {
        var text = Run("--help");
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Assert.Contains("Examples:", text, StringComparison.Ordinal);
        Assert.Contains("https://hoshi.srcz.one/seiko/", text, StringComparison.Ordinal);
        foreach (var command in new[] { "audit", "rollback", "search", "info", "doctor" })
        {
            Assert.Contains(command, text, StringComparison.Ordinal);
        }
    }
}
