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
    public void Doctor_ReportsBusyRoot()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        using var held = ProcessLock.Acquire(
            Path.Combine(fixture.Root, ".seiko", "install.lock"),
            "test root");

        var result = new Doctor().Run(fixture.Root, fixture.StoreDirectory);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("warning", finding.Severity);
        Assert.Equal("root-busy", finding.Code);
    }

    [Fact]
    public void Doctor_ReportsBusyArtifactInsteadOfMissing()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var client = new SeikoClient();
        client.Sync(fixture.Root, fixture.StoreDirectory);
        var digest = Assert.Single(client.ReadLock(fixture.Root)!.Sei).Artifact.Sha256;
        using var held = ProcessLock.Acquire(
            Path.Combine(fixture.StoreDirectory, ".locks", digest + ".lock"),
            "test artifact");

        var result = new Doctor().Run(fixture.Root, fixture.StoreDirectory);

        Assert.Contains(result.Findings, finding => finding.Code == "store-busy");
        Assert.DoesNotContain(result.Findings, finding => finding.Code == "store-missing");
    }

    [Fact]
    public void Doctor_ReportsNullFilesStateAsFinding()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var stateDirectory = Path.Combine(fixture.Root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(
            Path.Combine(stateDirectory, "state.json"),
            $$"""{"schemaVersion":{{LocalState.CurrentSchemaVersion}},"generation":1,"files":null}""");

        var result = new Doctor().Run(fixture.Root, fixture.StoreDirectory);

        Assert.Contains(result.Findings, finding => finding.Code == "deploy-state");
    }

    [Fact]
    public void Doctor_ReportsAnOldStateSchema()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var stateDirectory = Path.Combine(fixture.Root, ".seiko");
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(
            Path.Combine(stateDirectory, "state.json"),
            """{"schemaVersion":0,"generation":1,"files":{}}""");

        var result = new Doctor().Run(fixture.Root, fixture.StoreDirectory);

        var finding = Assert.Single(
            result.Findings,
            finding => finding.Code == "deploy-state");
        Assert.Contains("unsupported deployment state schemaVersion", finding.Message);
        Assert.Equal("error", finding.Severity);
    }

    [Fact]
    public void Doctor_ReportsNullDeployedFileRecord()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        var statePath = Path.Combine(fixture.Root, ".seiko", "state.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(statePath))!;
        foreach (var key in node["files"]!.AsObject().Select(pair => pair.Key).ToList())
        {
            node["files"]![key] = null;
        }

        File.WriteAllText(statePath, node.ToJsonString());

        var result = new Doctor().Run(fixture.Root, fixture.StoreDirectory);

        Assert.Contains(result.Findings, finding => finding.Code == "deploy-state");
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
    public void Doctor_CleanReportsBusyRoot()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        using var held = ProcessLock.Acquire(
            Path.Combine(fixture.Root, ".seiko", "install.lock"),
            "test install root");

        var result = new Doctor().Run(fixture.Root, fixture.StoreDirectory, clean: true);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("root-busy", finding.Code);
    }
}
