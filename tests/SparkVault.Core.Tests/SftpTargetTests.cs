using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class SftpTargetTests
{
    private const string Host = "127.0.0.1";
    private const int Port = 2222;

    // atmoz/sftp with command "testuser:testpass:::upload" chroots the user and exposes a
    // writable "upload" directory — remote paths in these tests must live under /upload.

    private static BackupTarget NewTestConfig() => new()
    {
        Type = TargetType.Sftp,
        Host = Host,
        Port = Port,
        Username = "testuser",
        EncryptedPassword = CredentialProtector.Protect("testpass"),
        RemotePath = $"/upload/test-{Guid.NewGuid():N}",
    };

    [Fact]
    public async Task UploadAsync_UploadsVerifiesAndListsFile()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-sftp-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            await File.WriteAllTextAsync(filePath, "hello sftp");
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length, new FileInfo(filePath).LastWriteTimeUtc);

            await using var target = new SftpTarget(NewTestConfig());

            Assert.True(await target.TestConnectionAsync(CancellationToken.None));
            await target.UploadAsync(file, progress: null, CancellationToken.None);

            var listed = (await target.ListExistingAsync(CancellationToken.None)).ToList();
            Assert.Contains(listed, f => f.Path == "a.txt" && f.Size == file.Size);

            await target.DeleteAsync("a.txt", CancellationToken.None);
            listed = (await target.ListExistingAsync(CancellationToken.None)).ToList();
            Assert.DoesNotContain(listed, f => f.Path == "a.txt");
        }
        finally
        {
            srcDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UploadAsync_NestedRelativePath_CreatesSubfolder()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-sftp-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "b.txt");
            await File.WriteAllTextAsync(filePath, "nested");
            var file = new BackupFile(filePath, "sub\\b.txt", new FileInfo(filePath).Length, new FileInfo(filePath).LastWriteTimeUtc);

            await using var target = new SftpTarget(NewTestConfig());
            await target.UploadAsync(file, progress: null, CancellationToken.None);

            var listed = (await target.ListExistingAsync(CancellationToken.None)).ToList();
            Assert.Contains(listed, f => f.Path == "sub/b.txt");
        }
        finally
        {
            srcDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UploadAsync_OnPreCancellation_LeavesNoFileBehind()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-sftp-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "c.txt");
            await File.WriteAllTextAsync(filePath, "cancel me");
            var file = new BackupFile(filePath, "c.txt", new FileInfo(filePath).Length, new FileInfo(filePath).LastWriteTimeUtc);

            await using var target = new SftpTarget(NewTestConfig());
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => target.UploadAsync(file, progress: null, cts.Token));

            var listed = (await target.ListExistingAsync(CancellationToken.None)).ToList();
            Assert.DoesNotContain(listed, f => f.Path == "c.txt");
        }
        finally
        {
            srcDir.Delete(recursive: true);
        }
    }

    // Guards the atomic POSIX rename: the commit step no longer deletes finalPath first, so a
    // re-upload over an existing file has to still land the new content (and leave no temp file).
    [Fact]
    public async Task UploadAsync_OverExistingFile_ReplacesContent()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-sftp-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "d.txt");
            await using var target = new SftpTarget(NewTestConfig());

            await File.WriteAllTextAsync(filePath, "old");
            await target.UploadAsync(new BackupFile(filePath, "d.txt", new FileInfo(filePath).Length, new FileInfo(filePath).LastWriteTimeUtc),
                progress: null, CancellationToken.None);

            await File.WriteAllTextAsync(filePath, "new content");
            await target.UploadAsync(new BackupFile(filePath, "d.txt", new FileInfo(filePath).Length, new FileInfo(filePath).LastWriteTimeUtc),
                progress: null, CancellationToken.None);

            var listed = (await target.ListExistingAsync(CancellationToken.None)).ToList();
            Assert.Single(listed);
            Assert.Equal("d.txt", listed[0].Path);
            Assert.Equal(new FileInfo(filePath).Length, listed[0].Size);
        }
        finally
        {
            srcDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_UploadedFile_WritesIdenticalContentToDestination()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-sftp-src-");
        var restoreDir = Directory.CreateTempSubdirectory("sparkvault-sftp-restore-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            await File.WriteAllTextAsync(filePath, "hello sftp download");
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length, new FileInfo(filePath).LastWriteTimeUtc);

            await using var target = new SftpTarget(NewTestConfig());
            await target.UploadAsync(file, progress: null, CancellationToken.None);

            var restoredPath = Path.Combine(restoreDir.FullName, "restored-a.txt");
            await target.DownloadAsync("a.txt", restoredPath, CancellationToken.None);

            Assert.Equal("hello sftp download", await File.ReadAllTextAsync(restoredPath));

            await target.DeleteAsync("a.txt", CancellationToken.None);
        }
        finally
        {
            srcDir.Delete(recursive: true);
            restoreDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task MoveAsync_UploadedFile_MovesToNewRelativePath()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-sftp-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            await File.WriteAllTextAsync(filePath, "move me sftp");
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length, new FileInfo(filePath).LastWriteTimeUtc);

            await using var target = new SftpTarget(NewTestConfig());
            await target.UploadAsync(file, progress: null, CancellationToken.None);

            await target.MoveAsync("a.txt", "_deleted/20260824-100000/a.txt", CancellationToken.None);

            var listed = (await target.ListExistingAsync(CancellationToken.None)).ToList();
            Assert.DoesNotContain(listed, f => f.Path == "a.txt");
            Assert.Contains(listed, f => f.Path == "_deleted/20260824-100000/a.txt");
        }
        finally
        {
            srcDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TestConnectionAsync_WrongCredentials_ReturnsFalse()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var config = NewTestConfig();
        config.EncryptedPassword = CredentialProtector.Protect("wrong-password");

        await using var target = new SftpTarget(config);

        Assert.False(await target.TestConnectionAsync(CancellationToken.None));
    }
}
