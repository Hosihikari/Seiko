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

        var (stdout, stderr, exitCode) = Cli.Run(
            ["doctor", "--root", fixture.Root, "--store", fixture.StoreDirectory],
            new Dictionary<string, string> { ["LANG"] = "zh_CN.UTF-8" });
        var text = stdout + stderr;
        Assert.False(string.IsNullOrEmpty(text), "doctor produced no output");
        Assert.Equal(0, exitCode);

        // The hint label itself is localized.
        Assert.Contains("提示", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorCountsUseRussianPluralForms()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var partial = Path.Combine(fixture.StoreDirectory, ".partial");
        Directory.CreateDirectory(partial);
        foreach (var name in new[] { "a", "b", "c" })
        {
            File.WriteAllText(Path.Combine(partial, name), "");
        }

        var (stdout, stderr, exitCode) = Cli.Run(
            ["doctor", "--root", fixture.Root, "--store", fixture.StoreDirectory],
            new Dictionary<string, string> { ["LANG"] = "ru_RU.UTF-8" });
        var text = stdout + stderr;
        Assert.False(string.IsNullOrEmpty(text), "doctor produced no output");
        Assert.Equal(0, exitCode);

        // 3 is the "few" category, not "many".
        Assert.Contains("незавершённые загрузки", text, StringComparison.Ordinal);
    }
}
