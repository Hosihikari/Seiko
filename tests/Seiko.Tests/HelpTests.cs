using Xunit;

namespace Seiko.Tests;

// Help is user-facing contract: every core command shows at least one example
// and the docs link.
public sealed class HelpTests
{
    [Fact]
    public void CoreCommandsShowExamplesAndDocsLinks()
    {
        var (stdout, stderr, _) = Cli.Run(["--help"]);
        var text = stdout + stderr;
        Assert.False(string.IsNullOrEmpty(text), "sk --help produced no output");

        Assert.Contains("Examples:", text, StringComparison.Ordinal);
        Assert.Contains("https://hoshi.srcz.one/seiko/", text, StringComparison.Ordinal);
        foreach (var command in new[] { "audit", "rollback", "search", "info", "doctor" })
        {
            Assert.Contains(command, text, StringComparison.Ordinal);
        }
    }
}
