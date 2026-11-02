using System.Diagnostics;
using Xunit;

namespace Seiko.Tests;

public sealed class ProcessLockTests
{
    [Fact]
    public void AcquireWaiting_ReportsPermissionErrorsImmediately()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "seiko-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "test.lock");
        File.WriteAllText(path, "");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(path, FileAttributes.ReadOnly);
            }
            else
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead);
            }

            var watch = Stopwatch.StartNew();
            var error = Assert.Throws<SeikoException>(
                () => ProcessLock.AcquireWaiting(path, "test lock"));

            Assert.Equal("io", error.Code);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AcquireWaiting_WaitsForALockInUse()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "seiko-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "test.lock");
        var held = ProcessLock.Acquire(path, "test lock");
        try
        {
            using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Task.Run(() => ProcessLock.AcquireWaiting(
                    path,
                    "test lock",
                    cancellationToken: canceled.Token)));
        }
        finally
        {
            held.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
