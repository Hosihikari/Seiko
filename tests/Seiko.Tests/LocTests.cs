using Xunit;

namespace Seiko.Tests;

// The locale comes from the environment (the AOT binary is invariant), and an
// unsupported language falls back to English. JSON stays English.
public sealed class LocTests
{
    [Fact]
    public void ChineseMappingIncludesTraditionalVariants()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");

        var (stdout, stderr, exitCode) = Cli.Run(
            ["doctor", "--root", fixture.Root, "--store", fixture.StoreDirectory],
            new Dictionary<string, string> { ["LANG"] = "zh_TW.UTF-8" });
        var text = stdout + stderr;
        Assert.False(string.IsNullOrEmpty(text), "doctor produced no output");
        Assert.Equal(0, exitCode);

        Assert.Contains("提示：", text, StringComparison.Ordinal);
        Assert.Contains("没有锁", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ArabicHintsAreTranslated()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");

        var (stdout, stderr, exitCode) = Cli.Run(
            ["doctor", "--root", fixture.Root, "--store", fixture.StoreDirectory],
            new Dictionary<string, string> { ["LANG"] = "ar_EG.UTF-8" });
        var text = stdout + stderr;
        Assert.False(string.IsNullOrEmpty(text), "doctor produced no output");
        Assert.Equal(0, exitCode);

        Assert.Contains("تلميح:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownLanguageFallsBackToEnglish()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");

        var (stdout, stderr, exitCode) = Cli.Run(
            ["doctor", "--root", fixture.Root, "--store", fixture.StoreDirectory],
            new Dictionary<string, string> { ["LANG"] = "de_DE.UTF-8" });
        var text = stdout + stderr;
        Assert.False(string.IsNullOrEmpty(text), "doctor produced no output");
        Assert.Equal(0, exitCode);

        Assert.Contains("hint:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorMessagesAreTranslated()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");

        var (stdout, stderr, exitCode) = Cli.Run(
            ["doctor", "--root", fixture.Root, "--store", fixture.StoreDirectory],
            new Dictionary<string, string> { ["LANG"] = "zh_CN.UTF-8" });
        var text = stdout + stderr;
        Assert.False(string.IsNullOrEmpty(text), "doctor produced no output");
        Assert.Equal(0, exitCode);

        Assert.Contains("没有锁", text, StringComparison.Ordinal);
        Assert.DoesNotContain("no lock;", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ArabicWrapsMachineTokensWithIsolates()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");

        var (stdout, stderr, exitCode) = Cli.Run(
            ["doctor", "--root", fixture.Root, "--store", fixture.StoreDirectory],
            new Dictionary<string, string> { ["LANG"] = "ar_EG.UTF-8" });
        var text = stdout + stderr;
        Assert.False(string.IsNullOrEmpty(text), "doctor produced no output");
        Assert.Equal(0, exitCode);

        // FSI/PDI keep the English hint from being reordered by the bidi
        // algorithm (ADR-0034).
        Assert.Contains(((char)0x2068).ToString(), text, StringComparison.Ordinal);
        Assert.Contains(((char)0x2069).ToString(), text, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonStaysEnglish()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");

        var (stdout, _, exitCode) = Cli.Run(
            ["doctor", "--root", fixture.Root, "--store", fixture.StoreDirectory, "--json"],
            new Dictionary<string, string> { ["LANG"] = "zh_CN.UTF-8" });

        Assert.False(string.IsNullOrEmpty(stdout), "doctor --json produced no output");
        Assert.Equal(0, exitCode);
        Assert.Contains("\"hint\":", stdout, StringComparison.Ordinal);
        Assert.Contains("\"message\":\"no lock", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("提示", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("messageKey", stdout, StringComparison.Ordinal);
    }
}
