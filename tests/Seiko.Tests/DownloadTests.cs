using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Seiko.Tests;

public sealed class DownloadTests
{
    [Fact]
    public async Task HttpSource_DownloadsLargeArtifactInRanges()
    {
        using var fixture = new Fixture();
        var data = new byte[5000];
        Random.Shared.NextBytes(data);
        var artifactDirectory = Path.Combine(fixture.SourceDirectory, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var artifactPath = Path.Combine(artifactDirectory, "blob.bin");
        await File.WriteAllBytesAsync(artifactPath, data);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.SourceDirectory, "index.json"),
            """{"schemaVersion":1,"generatedBy":"test","sei":[]}""");

        using var server = new TestHttpServer(fixture.SourceDirectory);
        using var session = new DownloadSession(4, CancellationToken.None, chunkThreshold: 1024);
        using var source = new HttpSource(
            new SourceConfig { Id = "test", Index = server.BaseUrl + "index.json" },
            proxy: null,
            session);
        var downloadDirectory = Path.Combine(fixture.Root, "download");
        Directory.CreateDirectory(downloadDirectory);
        var sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        await source.DownloadAsync(
            "hello",
            new ArtifactLocation { Url = "artifacts/blob.bin", Sha256 = sha, Size = data.Length },
            downloadDirectory,
            session,
            CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(downloadDirectory, "artifact")));
        var state = JsonSerializer.Deserialize(
            await File.ReadAllBytesAsync(Path.Combine(downloadDirectory, "meta.json")),
            SeikoJsonContext.Default.PartialArtifactState);
        Assert.Equal(4, state!.SegmentCount);
    }

    [Fact]
    public async Task HttpSource_ErrorRedactsArtifactQuery()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.SourceDirectory, "index.json"),
            """{"schemaVersion":1,"generatedBy":"test","sei":[]}""");
        using var server = new TestHttpServer(fixture.SourceDirectory);
        using var session = new DownloadSession(4, CancellationToken.None);
        using var source = new HttpSource(
            new SourceConfig { Id = "test", Index = server.BaseUrl + "index.json" },
            proxy: null,
            session);
        var downloadDirectory = Path.Combine(fixture.Root, "download");
        Directory.CreateDirectory(downloadDirectory);

        var error = await Assert.ThrowsAsync<SeikoException>(
            () => source.DownloadAsync(
                "hello",
                new ArtifactLocation
                {
                    Url = "artifacts/missing.bin?sig=REVIEW_SENTINEL",
                    Sha256 = new string('a', 64),
                    Size = 1,
                },
                downloadDirectory,
                session,
                CancellationToken.None));

        Assert.DoesNotContain("REVIEW_SENTINEL", error.Message);
        Assert.Contains("artifacts/missing.bin", error.Message);
    }

    [Fact]
    public async Task HttpSource_RetriesTruncatedBody()
    {
        using var fixture = new Fixture();
        var data = new byte[5000];
        Random.Shared.NextBytes(data);
        var artifactDirectory = Path.Combine(fixture.SourceDirectory, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        await File.WriteAllBytesAsync(Path.Combine(artifactDirectory, "blob.bin"), data);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.SourceDirectory, "index.json"),
            """{"schemaVersion":1,"generatedBy":"test","sei":[]}""");

        using var server = new TestHttpServer(fixture.SourceDirectory)
        {
            TruncateBodyResponses = 1,
            TruncateBodyPath = "artifacts",
        };
        using var session = new DownloadSession(4, CancellationToken.None, chunkThreshold: 1_000_000);
        using var source = new HttpSource(
            new SourceConfig { Id = "test", Index = server.BaseUrl + "index.json" },
            proxy: null,
            session);
        var downloadDirectory = Path.Combine(fixture.Root, "download");
        Directory.CreateDirectory(downloadDirectory);

        await source.DownloadAsync(
            "hello",
            new ArtifactLocation
            {
                Url = "artifacts/blob.bin",
                Sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),
                Size = data.Length,
            },
            downloadDirectory,
            session,
            CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(downloadDirectory, "artifact")));
        Assert.Equal(0, server.TruncateBodyResponses);
    }

    [Fact]
    public async Task HttpSource_ResumesPartialDownload()
    {
        using var fixture = new Fixture();
        var data = new byte[5000];
        Random.Shared.NextBytes(data);
        var artifactDirectory = Path.Combine(fixture.SourceDirectory, "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        await File.WriteAllBytesAsync(Path.Combine(artifactDirectory, "blob.bin"), data);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.SourceDirectory, "index.json"),
            """{"schemaVersion":1,"generatedBy":"test","sei":[]}""");

        using var server = new TestHttpServer(fixture.SourceDirectory);
        using var session = new DownloadSession(4, CancellationToken.None, chunkThreshold: 1024);
        using var source = new HttpSource(
            new SourceConfig { Id = "test", Index = server.BaseUrl + "index.json" },
            proxy: null,
            session);
        var downloadDirectory = Path.Combine(fixture.Root, "download");
        Directory.CreateDirectory(downloadDirectory);
        var state = new PartialArtifactState
        {
            Url = server.BaseUrl + "artifacts/blob.bin",
            Size = data.Length,
            ETag = "\"" + Convert.ToHexString(SHA256.HashData(data)) + "\"",
            SegmentCount = 4,
            SegmentSize = (data.Length + 3) / 4,
        };
        await File.WriteAllBytesAsync(
            Path.Combine(downloadDirectory, "meta.json"),
            JsonSerializer.SerializeToUtf8Bytes(state, SeikoJsonContext.Default.PartialArtifactState));
        await File.WriteAllBytesAsync(Path.Combine(downloadDirectory, "seg-0"), data[..600]);

        await source.DownloadAsync(
            "hello",
            new ArtifactLocation
            {
                Url = "artifacts/blob.bin",
                Sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),
                Size = data.Length,
            },
            downloadDirectory,
            session,
            CancellationToken.None);

        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(downloadDirectory, "artifact")));
    }

    [Fact]
    public void Sync_ReportsProgressEvents()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture { Files = { ["hello.txt"] = "hello" } }],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        var events = new List<SyncProgress>();

        new SeikoClient().Sync(
            fixture.Root,
            fixture.StoreDirectory,
            progress: new InlineProgress<SyncProgress>(events.Add));

        Assert.Contains(events, item => item.Phase == SyncPhase.Resolved && item.SeiTotal == 1);
        Assert.Contains(events, item => item.Phase == SyncPhase.Downloading && item.Name == "hello");
        Assert.Contains(events, item => item.Phase == SyncPhase.Completed && item.SeiCompleted == 1);
        Assert.Contains(events, item => item.Phase == SyncPhase.Deploying);
    }

    [Fact]
    public void Sync_RejectsConcurrentInstallRoot()
    {
        using var fixture = new Fixture();
        fixture.AddSei("hello", new VersionFixture
        {
            Version = "1.0.0",
            Artifacts = [new ArtifactFixture()],
        });
        fixture.WriteConfig("win-x64", new SpecFixture("hello", "[1.0.0]"));
        using var held = ProcessLock.Acquire(
            Path.Combine(fixture.Root, ".seiko", "install.lock"),
            "test install root");

        var error = Assert.Throws<SeikoException>(
            () => new SeikoClient().Sync(fixture.Root, fixture.StoreDirectory));

        Assert.Equal("lock", error.Code);
    }

    [Fact]
    public void DownloadSession_RejectsInvalidJobs()
    {
        var error = Assert.Throws<SeikoException>(() => new DownloadSession(0, CancellationToken.None));
        Assert.Equal("usage", error.Code);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
