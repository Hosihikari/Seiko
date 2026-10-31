using Xunit;

namespace Seiko.Tests;

public sealed class ResolverDepthTests
{
    [Fact]
    public void Resolve_FailsAtTheDepthBudgetInsteadOfOverflowing()
    {
        var source = new ChainSource(600);
        var resolver = new Resolver([source], null, "win-x64", false);

        var error = Assert.Throws<SeikoException>(
            () => resolver.Resolve([new SeiSpec { Name = "p0" }]));

        Assert.Equal("resolve", error.Code);
        Assert.Contains("depth budget", error.Message);
    }

    private sealed class ChainSource : ISeiSource
    {
        private readonly Dictionary<string, SeiIndex> _indexes = new(StringComparer.Ordinal);

        public ChainSource(int depth)
        {
            var names = new List<string>();
            for (var i = 0; i < depth; i++)
            {
                var name = $"p{i}";
                names.Add(name);
                var entry = new SeiVersionEntry { Version = "1.0.0" };
                if (i + 1 < depth)
                {
                    entry.Dependencies.Add(new SeiDependency
                    {
                        Name = $"p{i + 1}",
                        Version = "*",
                    });
                }

                _indexes[name] = new SeiIndex { Name = name, Versions = [entry] };
            }

            Index = new IndexRoot { Sei = names };
        }

        public string Id => "test";

        public IndexRoot Index { get; }

        public SeiIndex? GetSei(string name) => _indexes.GetValueOrDefault(name);

        public string ResolveArtifact(ArtifactLocation source)
            => throw new NotSupportedException();

        public Task DownloadAsync(
            string name,
            ArtifactLocation source,
            string directory,
            DownloadSession session,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
