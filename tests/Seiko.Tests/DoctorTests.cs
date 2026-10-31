using Xunit;

namespace Seiko.Tests;

public sealed class DoctorTests
{
    [Fact]
    public void Doctor_HealthyAfterSync()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        var result = new Doctor().Run(fixture.Root, fixture.StoreDirectory);

        Assert.DoesNotContain(result.Findings, finding => finding.Severity == "error");
    }

    [Fact]
    public void Doctor_ReportsMissingDeployedFile()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        File.Delete(Path.Combine(fixture.Root, "hello.txt"));

        var result = new Doctor().Run(fixture.Root, fixture.StoreDirectory);

        Assert.Contains(result.Findings, finding => finding.Code == "deploy-missing-file");
    }

    [Fact]
    public void Doctor_CleanRemovesPartials()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var partial = Path.Combine(fixture.StoreDirectory, ".partial", "stale");
        Directory.CreateDirectory(partial);

        var result = new Doctor().Run(fixture.Root, fixture.StoreDirectory, clean: true);

        Assert.False(Directory.Exists(partial));
        Assert.Equal(1, result.CleanCount);
    }

    [Fact]
    public void Doctor_CleanSkipsPartialsHeldByDownload()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var artifact = new string('a', 64);
        var partial = Path.Combine(fixture.StoreDirectory, ".partial", artifact);
        Directory.CreateDirectory(partial);
        using var held = ProcessLock.Acquire(
            Path.Combine(fixture.StoreDirectory, ".locks", artifact + ".lock"),
            "test artifact");

        var result = new Doctor().Run(fixture.Root, fixture.StoreDirectory, clean: true);

        Assert.True(Directory.Exists(partial));
        Assert.Equal(0, result.CleanCount);
        Assert.Contains(result.Findings, finding => finding.Code == "partial-in-use");
    }

    [Fact]
    public void Doctor_ReportsIncompleteDeploymentTransaction()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var stateDirectory = Path.Combine(fixture.Root, ".seiko");
        var transaction = DeploymentTransaction.Begin(fixture.Root, stateDirectory);
        transaction.WriteJournal();

        var result = new Doctor().Run(fixture.Root, fixture.StoreDirectory);

        Assert.Contains(result.Findings, finding => finding.Code == "deploy-transaction");
        DeploymentTransaction.Recover(fixture.Root, stateDirectory);
    }

    [Fact]
    public void Doctor_CleanRequiresInstallRootLock()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        using var held = ProcessLock.Acquire(
            Path.Combine(fixture.Root, ".seiko", "install.lock"),
            "test install root");

        var error = Assert.Throws<SeikoException>(
            () => new Doctor().Run(fixture.Root, fixture.StoreDirectory, clean: true));

        Assert.Equal("lock", error.Code);
    }
}
