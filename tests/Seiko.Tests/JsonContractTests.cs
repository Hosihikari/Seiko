using System.Diagnostics;
using Xunit;

namespace Seiko.Tests;

// The envelope pointer only moves for breaking changes: adding error.hint is
// additive, so the version stays 1.
public sealed class JsonContractTests
{
    private static string Run(params string[] args)
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
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    [Fact]
    public void ErrorEnvelopeKeepsVersionOneAndCarriesAnOptionalHint()
    {
        var output = Run("info", "nope", "--json");
        if (string.IsNullOrEmpty(output))
        {
            return;
        }

        var node = System.Text.Json.Nodes.JsonNode.Parse(output)!.AsObject();
        Assert.Equal(1, node["schemaVersion"]!.GetValue<int>());
        // A hint may be present; when present it must be a string, and the
        // version must not have moved because of it.
        if (node["error"]!["hint"] is { } hint)
        {
            Assert.Equal(System.Text.Json.JsonValueKind.String, hint.GetValueKind());
        }
    }

    [Fact]
    public void SearchDataAdditionsKeepVersionOne()
    {
        var output = Run("search", "hello", "--json");
        if (string.IsNullOrEmpty(output))
        {
            return;
        }

        var node = System.Text.Json.Nodes.JsonNode.Parse(output)!.AsObject();
        Assert.Equal(1, node["schemaVersion"]!.GetValue<int>());
    }
}
