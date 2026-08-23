using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class FtpTargetTests
{
    private const string Host = "127.0.0.1";
    private const int Port = 2121;

    // ponytail: no Xunit.SkippableFact dependency yet — each test returns early (reports as a
    // trivial pass, not a failure) if `docker compose -f docker/docker-compose.test.yml up -d`
    // hasn't been run. Add SkippableFact (or move to xUnit v3's Assert.Skip) if a clearer
    // "skipped" signal in test output ever matters more than avoiding the extra dependency.

    private static BackupTarget NewTestConfig() => new()
    {
        Type = TargetType.Ftp,
        Host = Host,
        Port = Port,
        Username = "testuser",
        EncryptedPassword = CredentialProtector.Protect("testpass"),
        EncryptionMode = FtpEncryption.None,
        RemotePath = $"/test-{Guid.NewGuid():N}",
    };

    [Fact]
    public async Task UploadAsync_UploadsVerifiesAndListsFile()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-ftp-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            await File.WriteAllTextAsync(filePath, "hello ftp");
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length, new FileInfo(filePath).LastWriteTimeUtc);

            await using var target = new FtpTarget(NewTestConfig());

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
    public async Task UploadAsync_DotfileName_UploadsSuccessfully()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        // Regression: the test server (pure-ftpd-based) hides dotfiles from LIST, and
        // GetObjectInfo falls back to a LIST-based lookup when MLST isn't supported (this
        // server doesn't support it), so verification used to fail with "erhalten -1" for any
        // dotfile-named upload (e.g. a real .gitignore in a backed-up source tree).
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-ftp-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, ".gitignore");
            await File.WriteAllTextAsync(filePath, "bin/\nobj/\n");
            var file = new BackupFile(filePath, ".gitignore", new FileInfo(filePath).Length, new FileInfo(filePath).LastWriteTimeUtc);

            await using var target = new FtpTarget(NewTestConfig());
            await target.UploadAsync(file, progress: null, CancellationToken.None);
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

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-ftp-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "b.txt");
            await File.WriteAllTextAsync(filePath, "nested");
            // RelativePath uses a Windows-style backslash, exactly what FileScanner produces on Windows.
            var file = new BackupFile(filePath, "sub\\b.txt", new FileInfo(filePath).Length, new FileInfo(filePath).LastWriteTimeUtc);

            await using var target = new FtpTarget(NewTestConfig());
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

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-ftp-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "c.txt");
            await File.WriteAllTextAsync(filePath, "cancel me");
            var file = new BackupFile(filePath, "c.txt", new FileInfo(filePath).Length, new FileInfo(filePath).LastWriteTimeUtc);

            await using var target = new FtpTarget(NewTestConfig());
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

    [Fact]
    public async Task TestConnectionAsync_WrongCredentials_ReturnsFalse()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var config = NewTestConfig();
        config.EncryptedPassword = CredentialProtector.Protect("wrong-password");

        await using var target = new FtpTarget(config);

        Assert.False(await target.TestConnectionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DownloadAsync_UploadedFile_WritesIdenticalContentToDestination()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-ftp-src-");
        var restoreDir = Directory.CreateTempSubdirectory("sparkvault-ftp-restore-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            await File.WriteAllTextAsync(filePath, "hello ftp download");
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length, new FileInfo(filePath).LastWriteTimeUtc);

            await using var target = new FtpTarget(NewTestConfig());
            await target.UploadAsync(file, progress: null, CancellationToken.None);

            var restoredPath = Path.Combine(restoreDir.FullName, "restored-a.txt");
            await target.DownloadAsync("a.txt", restoredPath, CancellationToken.None);

            Assert.Equal("hello ftp download", await File.ReadAllTextAsync(restoredPath));

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

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-ftp-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            await File.WriteAllTextAsync(filePath, "move me ftp");
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length, new FileInfo(filePath).LastWriteTimeUtc);

            await using var target = new FtpTarget(NewTestConfig());
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
}
