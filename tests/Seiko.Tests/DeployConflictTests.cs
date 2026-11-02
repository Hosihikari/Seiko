using Xunit;

namespace Seiko.Tests;

public sealed class DeployConflictTests
{
    [Fact]
    public void Sync_RejectsConflictingSharedPathModes()
    {
        using var fixture = new Fixture();
        fixture.AddSei("aaa", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["tool"] = "same" },
                    Modes = new Dictionary<string, string> { ["tool"] = "0644" },
                },
            ],
        });
        fixture.AddSei("bbb", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["tool"] = "same" },
                    Modes = new Dictionary<string, string> { ["tool"] = "0755" },
                },
            ],
        });
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("aaa", "[1.0.0]"),
            new SpecFixture("bbb", "[1.0.0]"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("deploy", error.Code);
        Assert.Contains("mode conflict", error.Message);
        Assert.Contains("tool", error.Message);
        Assert.Contains("0644", error.Message);
        Assert.Contains("0755", error.Message);
    }

    [Fact]
    public void Sync_AppliesSingleExplicitSharedPathMode()
    {
        using var fixture = new Fixture();
        fixture.AddSei("aaa", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture { Files = { ["tool"] = "same" } },
            ],
        });
        fixture.AddSei("bbb", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Files = { ["tool"] = "same" },
                    Modes = new Dictionary<string, string> { ["tool"] = "0755" },
                },
            ],
        });
        fixture.WriteConfig(
            "win-x64",
            new SpecFixture("aaa", "[1.0.0]"),
            new SpecFixture("bbb", "[1.0.0]"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                (UnixFileMode)Convert.ToInt32("755", 8),
                File.GetUnixFileMode(Path.Combine(fixture.Root, "tool")));
        }
    }
}
