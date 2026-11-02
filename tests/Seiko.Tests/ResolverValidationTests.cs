using Xunit;

namespace Seiko.Tests;

public sealed class ResolverValidationTests
{
    [Fact]
    public void Sync_RejectsSelfDependency()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Dependencies = [new DepFixture("hello", "*")],
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("resolve", error.Code);
        Assert.Contains("depends on itself", error.Message);
    }

    [Fact]
    public void Sync_RejectsDuplicateVersions()
    {
        using var fixture = new Fixture();
        fixture.AddSei(
            "hello",
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "first" } }],
            },
            new VersionFixture
            {
                Version = "1.0.0",
                Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "second" } }],
            });
        fixture.WriteConfig("win-x64", new SpecFixture("hello"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("resolve", error.Code);
        Assert.Contains("duplicate version", error.Message);
    }
}
