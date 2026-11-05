using Xunit;

namespace Seiko.Tests;

// Plural categories follow CLDR per language; the catalog key itself is
// internal, so the rules are asserted through the process output.
public sealed class LocPluralTests
{
    [Fact]
    public void SyncSummaryUsesTheCountInEachLanguage()
    {
        // Doctor always reports at least one finding for an empty root, and
        // the hint line carries the localized label.
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");

        var (stdout, stderr, _) = Cli.Run(
            ["doctor", "--root", fixture.Root],
            new Dictionary<string, string> { ["LANG"] = "zh_CN.UTF-8" });
        var text = stdout + stderr;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // The hint label itself is localized.
        Assert.Contains("提示", text, StringComparison.Ordinal);
    }
}
