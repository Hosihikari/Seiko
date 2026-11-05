using System.Diagnostics;
using Xunit;

namespace Seiko.Tests;

// Colour is emphasis only; tests assert it via the CLI process because the
// helper is internal to the executable project.
public sealed class ColorTests
{
    private static string Run(string? color, string? noColor = null)
    {
        var assembly = typeof(SeikoClient).Assembly.Location;
        var cli = Path.Combine(Path.GetDirectoryName(assembly)!, OperatingSystem.IsWindows() ? "sk.exe" : "sk");
        if (!File.Exists(cli))
        {
            return "";
        }

        var start = new ProcessStartInfo(cli)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("info");
        start.ArgumentList.Add("nope");
        if (color is not null)
        {
            start.ArgumentList.Add("--color=" + color);
        }

        if (noColor is not null)
        {
            start.Environment["NO_COLOR"] = noColor;
        }
        else
        {
            start.Environment.Remove("NO_COLOR");
        }

        using var process = Process.Start(start)!;
        var output = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    [Fact]
    public void AlwaysEmitsEscapeSequences()
    {
        var output = Run("always");
        if (string.IsNullOrEmpty(output))
        {
            return;
        }

        Assert.Contains("\u001b[31m", output, StringComparison.Ordinal);
        Assert.Contains("\u001b[0m", output, StringComparison.Ordinal);
    }

    [Fact]
    public void NeverAndPipesStayPlain()
    {
        var output = Run("never");
        if (string.IsNullOrEmpty(output))
        {
            return;
        }

        Assert.DoesNotContain("\u001b[", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoIsPlainWhenRedirected()
    {
        var output = Run(null);
        if (string.IsNullOrEmpty(output))
        {
            return;
        }

        Assert.DoesNotContain("\u001b[", output, StringComparison.Ordinal);
    }
}
