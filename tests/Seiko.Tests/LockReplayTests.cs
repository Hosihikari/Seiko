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
            var sourceA = CreateSource(Path.Combine(directory, "a"), "a", shaA, rid: "linux-x64");
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
    public void Resolve_RejectsAnArtifactForAnotherPlatform()
    {
        var directory = Path.Combine(Path.GetTempPath(), "seiko-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sha = new string('a', 64);
            var source = CreateSource(Path.Combine(directory, "a"), "a", sha, rid: "linux-x64");
            var lockFile = Lock("a", sha, artifactRid: "linux-x64");

            var error = Assert.Throws<SeikoException>(() => LockReplay.Resolve(
                [source],
                lockFile,
                [new SeiSpec { Name = "hello", Version = "[1.0.0]" }]));

            Assert.Equal("lock", error.Code);
            Assert.Contains("linux-x64", error.Message);
            Assert.Contains("win-x64", error.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Read_RejectsNullLockCollections()
    {
        var directory = Path.Combine(Path.GetTempPath(), "seiko-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "seiko.lock.json");
            File.WriteAllText(
                path,
                """{"schemaVersion":2,"target":{"rid":"win-x64"},"sources":[],"sei":null}""");

            var error = Assert.Throws<SeikoException>(() => LockFile.Read(path));

            Assert.Equal("lock", error.Code);
            Assert.Contains("invalid lock file", error.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Read_RejectsNullRelationElements()
    {
        var directory = Path.Combine(Path.GetTempPath(), "seiko-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var lockFile = Lock("a", new string('a', 64));
            lockFile.Sei[0].Dependencies = [null!];
            var path = Path.Combine(directory, "seiko.lock.json");
            LockFile.Write(path, lockFile);

            var error = Assert.Throws<SeikoException>(() => LockFile.Read(path));

            Assert.Equal("lock", error.Code);
            Assert.Contains("invalid lock file", error.Message);
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

    private static SeikoLock Lock(string source, string sha256, string? artifactRid = null)
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
                    Artifact = new LockedArtifact { Sha256 = sha256, Rid = artifactRid },
                },
            ],
        };

    private static ISeiSource CreateSource(
        string directory,
        string id,
        string sha256,
        string? rid = null)
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
                            Rid = rid,
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
