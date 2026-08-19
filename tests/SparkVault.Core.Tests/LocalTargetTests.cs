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
}
