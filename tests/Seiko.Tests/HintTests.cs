using Xunit;

namespace Seiko.Tests;

public sealed class HintTests
{
    [Fact]
    public void ResolveConflictCarriesAHint()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[2.0.0]"));

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.False(string.IsNullOrWhiteSpace(error.Hint));
        Assert.Contains("relax", error.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingSeiCarriesASearchHint()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture { Version = "1.0.0", Artifacts = [new ArtifactFixture()] });
        fixture.WriteConfig("win-x64");

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Info(fixture.Root, "nope"));

        Assert.Contains("sk search", error.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorFindingsCarryHints()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");

        var result = new Doctor().Run(fixture.Root, fixture.StoreDirectory);

        Assert.All(result.Findings, finding => Assert.False(string.IsNullOrWhiteSpace(finding.Hint)));
    }

    [Fact]
    public void TransactionFindingCarriesAHint()
    {
        using var fixture = new Fixture();
        fixture.WriteConfig("win-x64");
        var state = Path.Combine(fixture.Root, ".seiko");
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "transaction.json"), "{}");

        var result = new Doctor().Run(fixture.Root, fixture.StoreDirectory);

        var finding = Assert.Single(result.Findings, item => item.Code == "deploy-transaction");
        Assert.False(string.IsNullOrWhiteSpace(finding.Hint));
        Assert.Equal("hint.transaction", finding.HintKey);
    }
}
