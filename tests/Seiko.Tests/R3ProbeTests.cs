using Xunit;

namespace Seiko.Tests;

public sealed class R3ProbeTests
{
    [Fact]
    public void AuditLog_RedactsNonAsciiUrlSecrets()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var audit = Path.Combine(fixture.Root, "audit.jsonl");
        var server = new Seiko.McpServer.SeikoMcpServer(
            fixture.Root,
            allowMutations: true,
            auditLog: audit);

        var response = server.Handle(
            """
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"source_add","arguments":{"root":"ROOT","id":"demo","index":"http://demo:密码SECRET@127.0.0.1/index.json?token=QUERY"}}}
            """.Replace("ROOT", fixture.Root.Replace("\\", "\\\\"), StringComparison.Ordinal));

        Assert.NotNull(response);
        var text = File.ReadAllText(audit);
        Assert.DoesNotContain("SECRET", text);
        Assert.DoesNotContain("QUERY", text);
        Assert.DoesNotContain("密码", text);
        Assert.Contains("http://127.0.0.1/index.json", text);
    }

    [Fact]
    public void F2_AmbiguityIsFoundInEachCandidatesOwnGraph()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "marker",
            new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture { Files = { ["m1.txt"] = "1" } }] },
            new VersionFixture { Version = "2.0.0", Artifacts = [new ArtifactFixture { Files = { ["m2.txt"] = "2" } }] });
        fixture.AddSei("aaa", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture
                {
                    Dependencies = [new DepFixture("marker", "[1.0.0]")],
                    Prerequisites = [new DepFixture("marker", "[1.0.0]")],
                    Files = { ["a.txt"] = "one" },
                },
                new ArtifactFixture
                {
                    Dependencies = [new DepFixture("marker", "[2.0.0]")],
                    Prerequisites = [new DepFixture("marker", "[2.0.0]")],
                    Files = { ["a.txt"] = "two" },
                },
            ],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("aaa", "[1.0.0]"));

        // Both alternatives are satisfiable, each with its own marker version,
        // so declaration order must not decide between them.
        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("resolve", error.Code);
        Assert.Contains("ambiguous artifact", error.Message);
    }

    [Fact]
    public void F2_ALowerSpecificityAlternativeIsNotAmbiguous()
    {
        using var fixture = new Fixture();
        fixture.AddSei("marker", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["m.txt"] = "m" } }],
        });
        fixture.AddSei("aaa", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts =
            [
                new ArtifactFixture { Files = { ["a.txt"] = "plain" } },
                new ArtifactFixture
                {
                    Prerequisites = [new DepFixture("marker", "*")],
                    Files = { ["a.txt"] = "specific" },
                },
            ],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("aaa", "[1.0.0]"), new SpecFixture("marker", "*"));

        new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory);

        Assert.Equal("specific", File.ReadAllText(Path.Combine(fixture.Root, "a.txt")));
    }
}
