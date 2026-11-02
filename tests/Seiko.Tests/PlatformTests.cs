using System.Runtime.InteropServices;
using Xunit;

namespace Seiko.Tests;

public sealed class PlatformTests
{
    [Theory]
    [InlineData("linux", Architecture.Arm, "linux-arm")]
    [InlineData("linux", Architecture.Arm64, "linux-arm64")]
    [InlineData("linux", Architecture.X64, "linux-x64")]
    [InlineData("win", Architecture.X86, "win-x86")]
    [InlineData("win", Architecture.Arm64, "win-arm64")]
    [InlineData("osx", Architecture.Arm64, "osx-arm64")]
    [InlineData("osx", Architecture.X64, "osx-x64")]
    public void DefaultRidFor_MapsSupportedArchitectures(
        string os,
        Architecture architecture,
        string expected)
    {
        Assert.Equal(expected, SeikoClient.DefaultRidFor(os, architecture));
    }
}
