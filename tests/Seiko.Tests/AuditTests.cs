using Xunit;

namespace Seiko.Tests;

// Audit answers "is what I have installed known-bad"; a source that cannot
// answer yields `unknown` rather than silence.
public sealed class AuditTests
{
    private static Fixture Installed(SeiIndex? mutator = null, string version = "1.0.0")
    {
        var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "2.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", $"[{version}]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        return fixture;
    }

    private static SeiIndex Read(Fixture fixture)
        => System.Text.Json.JsonSerializer.Deserialize<SeiIndex>(
            File.ReadAllBytes(Path.Combine(fixture.SourceDirectory, "sei", "hello.json")),
            SeikoJsonContext.Default.SeiIndex)!;

    private static void Write(Fixture fixture, SeiIndex index)
        => File.WriteAllBytes(
            Path.Combine(fixture.SourceDirectory, "sei", "hello.json"),
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(index, SeikoJsonContext.Default.SeiIndex));

    [Fact]
    public void Audit_ReportsNothingForACleanInstall()
    {
        using var fixture = Installed();

        var result = new SeikoClient().Audit(fixture.Root, storeRoot: fixture.StoreDirectory);

        Assert.Empty(result.Findings);
        Assert.Empty(result.Unknown);
    }

    [Fact]
    public void Audit_ReportsTheInstalledVersionAdvisoryAndTheFix()
    {
        using var fixture = Installed();
        var index = Read(fixture);
        index.Versions.Single(entry => entry.Version == "1.0.0").Advisory = new AdvisoryInfo
        {
            Severity = "high",
            Url = "https://example.invalid/a",
        };
        Write(fixture, index);

        var finding = Assert.Single(
            new SeikoClient().Audit(fixture.Root, storeRoot: fixture.StoreDirectory).Findings);

        Assert.Equal("1.0.0", finding.Version);
        Assert.Equal("advisory", finding.Kind);
        Assert.Equal("high", finding.Severity);
        Assert.Equal("2.0.0", finding.Fix);
    }

    [Fact]
    public void Audit_UpdatesWhenTheNewestVersionIsAlsoAffected()
    {
        using var fixture = Installed();
        var index = Read(fixture);
        index.Versions.Single(entry => entry.Version == "1.0.0").Advisory = new AdvisoryInfo { Severity = "low" };
        index.Versions.Single(entry => entry.Version == "2.0.0").Advisory = new AdvisoryInfo { Severity = "low" };
        Write(fixture, index);

        var finding = Assert.Single(
            new SeikoClient().Audit(fixture.Root, storeRoot: fixture.StoreDirectory).Findings);

        Assert.Null(finding.Fix);
    }

    [Fact]
    public void Audit_ReportsYankAndDeprecationUnlessSecurityOnly()
    {
        using var fixture = Installed();
        var index = Read(fixture);
        var entry = index.Versions.Single(item => item.Version == "1.0.0");
        entry.Yanked = true;
        entry.Deprecated = "use 2.0.0";
        Write(fixture, index);

        var all = new SeikoClient().Audit(fixture.Root, storeRoot: fixture.StoreDirectory);
        Assert.Equal(2, all.Findings.Count);

        var security = new SeikoClient().Audit(
            fixture.Root,
            storeRoot: fixture.StoreDirectory,
            securityOnly: true);
        Assert.Empty(security.Findings);
    }

    [Fact]
    public void Audit_ReportsUnknownWhenTheVersionIsMissingFromTheIndex()
    {
        using var fixture = Installed();
        var index = Read(fixture);
        index.Versions.RemoveAll(entry => entry.Version == "1.0.0");
        Write(fixture, index);

        var result = new SeikoClient().Audit(fixture.Root, storeRoot: fixture.StoreDirectory);

        Assert.Empty(result.Findings);
        Assert.Equal(["hello"], result.Unknown);
    }

    [Fact]
    public void Audit_ReportsUnknownWhenTheSourceIsGone()
    {
        using var fixture = Installed();
        var index = Read(fixture);
        Write(fixture, index);
        Directory.Delete(Path.Combine(fixture.SourceDirectory, "sei"), recursive: true);

        var result = new SeikoClient().Audit(fixture.Root, storeRoot: fixture.StoreDirectory);

        Assert.Equal(["hello"], result.Unknown);
    }

    [Fact]
    public void Audit_UsesTheSourceRecordedInTheLock()
    {
        using var fixture = Installed();
        var index = Read(fixture);
        index.Versions.Single(entry => entry.Version == "1.0.0").Advisory = new AdvisoryInfo
        {
            Severity = "high",
            Url = "https://example.invalid/a",
        };
        Write(fixture, index);

        // A later source first in the list carries the same version without
        // the advisory; auditing it would falsely report a clean install.
        var other = Path.Combine(fixture.Root, "other");
        Directory.CreateDirectory(Path.Combine(other, "sei"));
        var copy = System.Text.Json.JsonSerializer.Deserialize<SeiIndex>(
            File.ReadAllBytes(Path.Combine(fixture.SourceDirectory, "sei", "hello.json")),
            SeikoJsonContext.Default.SeiIndex)!;
        copy.Versions.Single(entry => entry.Version == "1.0.0").Advisory = null;
        File.WriteAllBytes(
            Path.Combine(other, "index.json"),
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new IndexRoot { Sei = ["hello"] },
                SeikoJsonContext.Default.IndexRoot));
        File.WriteAllBytes(
            Path.Combine(other, "sei", "hello.json"),
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(copy, SeikoJsonContext.Default.SeiIndex));

        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            "schemaVersion = 2\n"
            + "sources = [\n"
            + $"  {{ id = \"other\", index = \"{Path.Combine(other, "index.json").Replace('\\', '/')}\" }},\n"
            + $"  {{ id = \"local\", index = \"{Path.Combine(fixture.SourceDirectory, "index.json").Replace('\\', '/')}\" }}\n"
            + "]\n"
            + "rid = \"win-x64\"\n"
            + "sei = [\n  { name = \"hello\", version = \"[1.0.0]\" }\n]\n");

        var result = new SeikoClient().Audit(fixture.Root, storeRoot: fixture.StoreDirectory);

        var finding = Assert.Single(result.Findings);
        Assert.Equal("advisory", finding.Kind);
        Assert.Equal("high", finding.Severity);
        Assert.Empty(result.Unknown);
    }

    [Fact]
    public void Audit_ReportsUnknownWhenTheLockedSourceIndexIsUnavailable()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("hello", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        File.Delete(Path.Combine(fixture.SourceDirectory, "index.json"));

        var result = new SeikoClient().Audit(fixture.Root, storeRoot: fixture.StoreDirectory);

        Assert.Empty(result.Findings);
        Assert.Equal(["hello"], result.Unknown);
    }

    [Fact]
    public void Audit_ReportsUnknownWhenTheSourceIndexIsMalformed()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("hello", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        File.WriteAllText(Path.Combine(fixture.SourceDirectory, "index.json"), "{invalid");

        var result = new SeikoClient().Audit(fixture.Root, storeRoot: fixture.StoreDirectory);

        Assert.Empty(result.Findings);
        Assert.Equal(["hello"], result.Unknown);
    }

    [Fact]
    public void Audit_ReportsUnknownWhenThePackageIndexIsMalformed()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("hello", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);
        File.WriteAllText(Path.Combine(fixture.SourceDirectory, "sei", "hello.json"), "{invalid");

        var result = new SeikoClient().Audit(fixture.Root, storeRoot: fixture.StoreDirectory);

        Assert.Empty(result.Findings);
        Assert.Equal(["hello"], result.Unknown);
    }

    [Fact]
    public void Audit_ReportsUnknownWhenVersionEntriesAreInvalid()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("hello", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        var path = Path.Combine(fixture.SourceDirectory, "sei", "hello.json");
        foreach (var body in new[]
        {
            "{\"schemaVersion\":2,\"name\":\"hello\",\"versions\":[null]}",
            "{\"schemaVersion\":2,\"name\":\"hello\",\"versions\":null}",
        })
        {
            File.WriteAllText(path, body);

            var result = new SeikoClient().Audit(fixture.Root, storeRoot: fixture.StoreDirectory);

            Assert.Empty(result.Findings);
            Assert.Equal(["hello"], result.Unknown);
        }
    }

    [Fact]
    public void Audit_SkipsAnUnavailableSourceNoLockEntryUses()
    {
        using var fixture = new Fixture();
        using var server = new TestHttpServer(fixture.SourceDirectory);
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.WriteHttpConfig(server.BaseUrl, "win-x64", new SpecFixture("hello", "[1.0.0]"));
        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            "schemaVersion = 2\n"
            + "sources = [\n"
            + $"  {{ id = \"broken\", index = \"{server.BaseUrl}missing/index.json\" }},\n"
            + $"  {{ id = \"http\", index = \"{server.BaseUrl}index.json\" }}\n"
            + "]\n"
            + "rid = \"win-x64\"\n"
            + "sei = [\n  { name = \"hello\", version = \"[1.0.0]\" }\n]\n");

        var result = new SeikoClient().Audit(fixture.Root, storeRoot: fixture.StoreDirectory);

        Assert.Empty(result.Findings);
        Assert.Empty(result.Unknown);
    }

    [Fact]
    public void Audit_RequiresALock()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Audit(fixture.Root, storeRoot: fixture.StoreDirectory));

        Assert.Equal("lock", error.Code);
    }
}
