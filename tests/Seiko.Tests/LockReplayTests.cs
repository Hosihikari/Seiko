using System.Text.Json;
using Xunit;

namespace Seiko.Tests;

public sealed class LockReplayTests
{
    [Fact]
    public void Resolve_UsesTheLockedSourceIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "seiko-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var shaA = new string('a', 64);
            var shaB = new string('b', 64);
            var sourceA = CreateSource(Path.Combine(directory, "a"), "a", shaA);
            var sourceB = CreateSource(Path.Combine(directory, "b"), "b", shaB);
            var lockFile = Lock("b", shaB);

            var resolved = LockReplay.Resolve(
                [sourceA, sourceB],
                lockFile,
                [new SeiSpec { Name = "hello", Version = "[1.0.0]" }]);

            var sei = Assert.Single(resolved);
            Assert.Equal("b", sei.Source.Id);
            Assert.Equal(shaB, sei.Artifact.Source.Sha256);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Resolve_RejectsAMissingLockedSource()
    {
        var directory = Path.Combine(Path.GetTempPath(), "seiko-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sourceA = CreateSource(Path.Combine(directory, "a"), "a", new string('a', 64));
            var lockFile = Lock("missing", new string('b', 64));

            var error = Assert.Throws<SeikoException>(() => LockReplay.Resolve(
                [sourceA],
                lockFile,
                [new SeiSpec { Name = "hello", Version = "[1.0.0]" }]));

            Assert.Equal("lock", error.Code);
            Assert.Contains("missing", error.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SeikoLock Lock(string source, string sha256)
        => new()
        {
            Target = new PlatformInfo { Rid = "win-x64" },
            Sources = ["a", "b"],
            Sei =
            [
                new LockedSei
                {
                    Name = "hello",
                    Version = "1.0.0",
                    Source = source,
                    Direct = true,
                    Artifact = new LockedArtifact { Sha256 = sha256 },
                },
            ],
        };

    private static ISeiSource CreateSource(string directory, string id, string sha256)
    {
        Directory.CreateDirectory(Path.Combine(directory, "sei"));
        File.WriteAllBytes(
            Path.Combine(directory, "index.json"),
            JsonSerializer.SerializeToUtf8Bytes(
                new IndexRoot { GeneratedBy = "test", Sei = ["hello"] },
                SeikoJsonContext.Default.IndexRoot));
        var index = new SeiIndex
        {
            Name = "hello",
            Versions =
            [
                new SeiVersionEntry
                {
                    Version = "1.0.0",
                    Artifacts =
                    [
                        new ArtifactEntry
                        {
                            Layout = "root",
                            Source = new ArtifactLocation
                            {
                                Url = "artifact.zip",
                                Sha256 = sha256,
                                Size = 1,
                            },
                        },
                    ],
                },
            ],
        };
        File.WriteAllBytes(
            Path.Combine(directory, "sei", "hello.json"),
            JsonSerializer.SerializeToUtf8Bytes(index, SeikoJsonContext.Default.SeiIndex));
        return new LocalSource(
            new SourceConfig { Id = id, Index = Path.Combine(directory, "index.json") },
            directory);
    }
}
