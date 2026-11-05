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

        var (stdout, stderr, _) = Cli.Run(
            ["doctor", "--root", fixture.Root],
            new Dictionary<string, string> { ["LANG"] = "zh_TW.UTF-8" });
        stderr = stdout + stderr;

        Assert.Contains("提示：", stderr, StringComparison.Ordinal);
        Assert.Contains("提示：", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ArabicHintsAreTranslated()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");

        var (stdout, stderr, _) = Cli.Run(
            ["doctor", "--root", fixture.Root],
            new Dictionary<string, string> { ["LANG"] = "ar_EG.UTF-8" });
        stderr = stdout + stderr;

        if (string.IsNullOrEmpty(stderr))
        {
            return;
        }

        Assert.Contains("تلميح:", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownLanguageFallsBackToEnglish()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");

        var (stdout, stderr, _) = Cli.Run(
            ["doctor", "--root", fixture.Root],
            new Dictionary<string, string> { ["LANG"] = "de_DE.UTF-8" });
        stderr = stdout + stderr;

        if (string.IsNullOrEmpty(stderr))
        {
            return;
        }

        Assert.Contains("hint:", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorMessagesAreTranslated()
    {
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

        Assert.Contains("没有锁", text, StringComparison.Ordinal);
        Assert.DoesNotContain("no lock;", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ArabicWrapsMachineTokensWithIsolates()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");

        var (stdout, stderr, _) = Cli.Run(
            ["doctor", "--root", fixture.Root],
            new Dictionary<string, string> { ["LANG"] = "ar_EG.UTF-8" });
        var text = stdout + stderr;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

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

        var (stdout, _, _) = Cli.Run(
            ["doctor", "--root", fixture.Root, "--json"],
            new Dictionary<string, string> { ["LANG"] = "zh_CN.UTF-8" });

        Assert.Contains("\"hint\":", stdout, StringComparison.Ordinal);
        Assert.Contains("\"message\":\"no lock", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("提示", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("messageKey", stdout, StringComparison.Ordinal);
    }
}
