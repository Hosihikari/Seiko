using Xunit;

namespace Seiko.Tests;

public sealed class LocalSourceTests
{
    [Fact]
    public void SourceIndexMayNameTheDirectory()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64");
        var directory = fixture.SourceDirectory.Replace('\\', '/');
        File.WriteAllText(
            Path.Combine(fixture.Root, "seiko.conf"),
            $"schemaVersion = 2\nsources = [\n  {{ id = \"local\", index = \"{directory}\" }}\n]\n");

        // A local source is a directory (design §7); the index inside it is
        // implied. The explicit file path stays valid.
        var status = Assert.Single(new SeikoClient().SourceStatus(
            fixture.Root,
            storeRoot: fixture.StoreDirectory));
        Assert.True(status.Reachable);
        Assert.Empty(status.Problems);
        Assert.Equal(1, status.SeiCount);

        var client = new SeikoClient();
        client.Add(fixture.Root, "hello@[1.0.0]", storeRoot: fixture.StoreDirectory);
        Assert.Equal(
            "hello",
            File.ReadAllText(Path.Combine(fixture.Root, "hello.txt")));
    }

    [Fact]
    public async Task DownloadAsync_AllowsHttpPrefixedRelativePaths()
    {
        using var fixture = new Fixture();
        var sourceRoot = fixture.SourceDirectory;
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "http-library.zip"), "one");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "http"));
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "http", "artifact.zip"), "two");
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "index.json"),
            """{"schemaVersion":2,"generatedBy":"test","sei":[]}""");
        var source = new LocalSource(
            new SourceConfig
            {
                Id = "test",
                Index = Path.Combine(sourceRoot, "index.json"),
            },
            sourceRoot);

        foreach (var (url, expected) in new[]
        {
            ("http-library.zip", "one"),
            ("http/artifact.zip", "two"),
        })
        {
            using var session = new DownloadSession(1, CancellationToken.None);
            var target = Path.Combine(fixture.Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(target);
            await source.DownloadAsync(
                "hello",
                new ArtifactLocation
                {
                    Url = url,
                    Sha256 = Hashing.Sha256Bytes(System.Text.Encoding.UTF8.GetBytes(expected)),
                    Size = expected.Length,
                },
                target,
                session,
                CancellationToken.None);

            Assert.Equal(
                expected,
                await File.ReadAllTextAsync(Path.Combine(target, "artifact")));
        }
    }

    [Fact]
    public async Task DownloadAsync_RejectsSizeMismatchBeforeCreatingOutput()
    {
        using var fixture = new Fixture();
        var artifactDirectory = Path.Combine(fixture.SourceDirectory, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(artifactDirectory, "blob.bin"),
            new byte[128]);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.SourceDirectory, "index.json"),
            """{"schemaVersion":2,"generatedBy":"test","sei":[]}""");
        var source = new LocalSource(
            new SourceConfig
            {
                Id = "test",
                Index = Path.Combine(fixture.SourceDirectory, "index.json"),
            },
            fixture.SourceDirectory);
        using var session = new DownloadSession(1, CancellationToken.None);
        var target = Path.Combine(fixture.Root, "download");
        Directory.CreateDirectory(target);

        var error = await Assert.ThrowsAsync<SeikoException>(() => source.DownloadAsync(
            "hello",
            new ArtifactLocation
            {
                Url = "artifacts/blob.bin",
                Sha256 = new string('a', 64),
                Size = 64,
            },
            target,
            session,
            CancellationToken.None));

        Assert.Equal("checksum", error.Code);
        Assert.Contains("size mismatch", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }
}
