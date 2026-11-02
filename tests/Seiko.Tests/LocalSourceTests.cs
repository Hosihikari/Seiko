using Xunit;

namespace Seiko.Tests;

public sealed class LocalSourceTests
{
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
            """{"schemaVersion":1,"generatedBy":"test","sei":[]}""");
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
