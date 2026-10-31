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
