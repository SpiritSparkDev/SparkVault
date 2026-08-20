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
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length);

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
    public async Task UploadAsync_NestedRelativePath_CreatesSubfolder()
    {
        if (!DockerTestHelper.IsReachable(Host, Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-ftp-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "b.txt");
            await File.WriteAllTextAsync(filePath, "nested");
            // RelativePath uses a Windows-style backslash, exactly what FileScanner produces on Windows.
            var file = new BackupFile(filePath, "sub\\b.txt", new FileInfo(filePath).Length);

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
            var file = new BackupFile(filePath, "c.txt", new FileInfo(filePath).Length);

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
}
