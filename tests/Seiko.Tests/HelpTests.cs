using Xunit;

namespace Seiko.Tests;

// Help is user-facing contract: every core command shows at least one example
// and the docs link.
public sealed class HelpTests
{
    [Fact]
    public void CoreCommandsShowExamplesAndDocsLinks()
    {
        var (stdout, stderr, exitCode) = Cli.Run(["--help"]);
        var text = stdout + stderr;
        Assert.False(string.IsNullOrEmpty(text), "sk --help produced no output");
        Assert.Equal(0, exitCode);

        Assert.Contains("Examples:", text, StringComparison.Ordinal);
        Assert.Contains("https://hoshi.srcz.one/seiko/", text, StringComparison.Ordinal);
        foreach (var command in new[] { "audit", "rollback", "search", "info", "doctor" })
        {
            Assert.Contains(command, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void HelpFollowsTheSystemLanguage()
    {
        var (stdout, stderr, exitCode) = Cli.Run(
            ["--help"],
            new Dictionary<string, string> { ["LANG"] = "zh_CN.UTF-8" });
        var text = stdout + stderr;
        Assert.False(string.IsNullOrEmpty(text), "localized help produced no output");
        Assert.Equal(0, exitCode);

        Assert.Contains("Seiko 通用包管理器", text, StringComparison.Ordinal);
        Assert.Contains("示例", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Write a seiko.conf template.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedLanguageHelpFallsBackToEnglish()
    {
        var (stdout, stderr, exitCode) = Cli.Run(
            ["--help"],
            new Dictionary<string, string> { ["LANG"] = "de_DE.UTF-8" });
        var text = stdout + stderr;
        Assert.False(string.IsNullOrEmpty(text), "fallback help produced no output");
        Assert.Equal(0, exitCode);

        Assert.Contains("Examples:", text, StringComparison.Ordinal);
        Assert.Contains("Write a seiko.conf template.", text, StringComparison.Ordinal);
    }
}
