using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class LocalTargetTests
{
    [Fact]
    public async Task UploadAsync_CopiesFileAndVerifiesSize()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            File.WriteAllText(filePath, "hello world");
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length);

            var target = new LocalTarget(destDir.FullName);
            await target.UploadAsync(file, progress: null, CancellationToken.None);

            var finalPath = Path.Combine(destDir.FullName, "a.txt");
            Assert.True(File.Exists(finalPath));
            Assert.Equal("hello world", File.ReadAllText(finalPath));
            Assert.False(File.Exists(finalPath + ".sparkvault-tmp"));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UploadAsync_OnCancellation_LeavesNoFileBehind()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            File.WriteAllText(filePath, "hello world");
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length);

            var target = new LocalTarget(destDir.FullName);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => target.UploadAsync(file, progress: null, cts.Token));

            var finalPath = Path.Combine(destDir.FullName, "a.txt");
            Assert.False(File.Exists(finalPath));
            Assert.False(File.Exists(finalPath + ".sparkvault-tmp"));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UploadAsync_CancelledMidCopy_LeavesNoFileBehind()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "big.bin");
            // ~64MB of data - large enough that CopyToAsync yields across multiple buffer writes,
            // giving a real window to cancel mid-copy rather than before any I/O starts.
            var data = new byte[64 * 1024 * 1024];
            new Random(42).NextBytes(data);
            await File.WriteAllBytesAsync(filePath, data);
            var file = new BackupFile(filePath, "big.bin", data.LongLength);

            var target = new LocalTarget(destDir.FullName);
            using var cts = new CancellationTokenSource();
            var tempPath = Path.Combine(destDir.FullName, "big.bin.sparkvault-tmp");

            var uploadTask = target.UploadAsync(file, progress: null, cts.Token);

            // Poll for the temp file to appear and grow past a threshold, proving the copy is
            // genuinely in progress, then cancel - deterministic, not a fixed sleep.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(tempPath) && new FileInfo(tempPath).Length > 1024 * 1024)
                {
                    cts.Cancel();
                    break;
                }
                await Task.Delay(5);
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => uploadTask);

            var finalPath = Path.Combine(destDir.FullName, "big.bin");
            Assert.False(File.Exists(finalPath));
            Assert.False(File.Exists(tempPath));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TestConnectionAsync_CreatesDestinationIfMissing()
    {
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
        destDir.Delete(recursive: true);
        try
        {
            var target = new LocalTarget(destDir.FullName);
            var ok = await target.TestConnectionAsync(CancellationToken.None);

            Assert.True(ok);
            Assert.True(Directory.Exists(destDir.FullName));
        }
        finally
        {
            if (Directory.Exists(destDir.FullName))
                destDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_UploadedFile_WritesIdenticalContentToDestination()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-local-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-local-dest-");
        var restoreDir = Directory.CreateTempSubdirectory("sparkvault-local-restore-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            await File.WriteAllTextAsync(filePath, "hello local download");
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length);

            await using var target = new LocalTarget(destDir.FullName);
            await target.UploadAsync(file, progress: null, CancellationToken.None);

            var restoredPath = Path.Combine(restoreDir.FullName, "restored-a.txt");
            await target.DownloadAsync("a.txt", restoredPath, CancellationToken.None);

            Assert.Equal("hello local download", await File.ReadAllTextAsync(restoredPath));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            restoreDir.Delete(recursive: true);
        }
    }
}
