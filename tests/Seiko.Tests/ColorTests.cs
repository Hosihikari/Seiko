using Xunit;

namespace Seiko.Tests;

// Colour is emphasis only; auto and never stay plain when redirected.
public sealed class ColorTests
{
    private static string Run(string? color)
    {
        var (stdout, stderr, _) = Cli.Run(
            color is null ? ["info", "nope"] : ["info", "nope", "--color=" + color]);
        return stdout + stderr;
    }

    [Fact]
    public void AlwaysEmitsEscapeSequences()
    {
        var output = Run("always");

        Assert.Contains("\u001b[31m", output, StringComparison.Ordinal);
        Assert.Contains("\u001b[0m", output, StringComparison.Ordinal);
    }

    [Fact]
    public void NeverAndPipesStayPlain()
    {
        Assert.DoesNotContain("\u001b[", Run("never"), StringComparison.Ordinal);
    }

    [Fact]
    public void AutoIsPlainWhenRedirected()
    {
        Assert.DoesNotContain("\u001b[", Run(null), StringComparison.Ordinal);
    }
}
