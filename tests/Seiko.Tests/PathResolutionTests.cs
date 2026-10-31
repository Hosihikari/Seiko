using Xunit;

namespace Seiko.Tests;

public sealed class PathResolutionTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("/absolute")]
    [InlineData("../up")]
    [InlineData("a/../b")]
    [InlineData("a//b")]
    [InlineData("./a")]
    public void Parse_RejectsInvalidRelativePaths(string value)
    {
        Assert.Throws<SeikoException>(() => RelativePath.Parse(value));
    }

    [Theory]
    [InlineData("a/b", true)]
    [InlineData("a b/c", true)]
    [InlineData("", false)]
    [InlineData("a\\b", false)]
    [InlineData("/absolute", false)]
    [InlineData("../up", false)]
    [InlineData("a//b", false)]
    [InlineData("./a", false)]
    [InlineData("a/../b", false)]
    public void IsSafeStrict_ChecksStoredPaths(string value, bool expected)
    {
        Assert.Equal(expected, RelativePath.IsSafeStrict(value));
    }

    [Fact]
    public void IsSafeStrict_RejectsWindowsInvalidFileNames()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.False(RelativePath.IsSafeStrict("a:b"));
    }

    [Fact]
    public void IsUnder_UsesDirectoryBoundaries()
    {
        var root = Path.Combine(Path.GetTempPath(), "seiko-root");

        Assert.True(RootedPath.IsUnder(root, root));
        Assert.True(RootedPath.IsUnder(Path.Combine(root, "a"), root));
        Assert.False(RootedPath.IsUnder(root + "x", root));

        var upper = root.ToUpperInvariant();
        Assert.Equal(OperatingSystem.IsWindows(), RootedPath.IsUnder(upper, root));
    }

    [Fact]
    public void Parse_NormalizesSeparators()
    {
        Assert.Equal("a/b/c", RelativePath.Parse("a\\b\\c").Value);
    }

    [Fact]
    public void Resolve_ReturnsPathUnderRoot()
    {
        using var fixture = new Fixture();

        var resolved = RootedPath.Resolve(fixture.Root, RelativePath.Parse("a/b"));

        Assert.Equal(Path.Combine(fixture.Root, "a", "b"), resolved);
    }

    [Fact]
    public void Resolve_RejectsLinkedParent()
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(Path.GetTempPath(), "seiko-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "link"), outside);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
            {
                return;
            }

            Assert.Throws<SeikoException>(
                () => RootedPath.Resolve(fixture.Root, RelativePath.Parse("link/canary")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}
