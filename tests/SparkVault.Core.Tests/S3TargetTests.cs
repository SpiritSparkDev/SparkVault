using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class S3TargetTests
{
    private const string Endpoint = "http://127.0.0.1:9000";
    private const int Port = 9000;

    // ponytail: no Xunit.SkippableFact dependency — each test returns early (reports as a
    // trivial pass, not a failure) if `docker compose -f docker/docker-compose.test.yml up -d`
    // hasn't been run, same pattern as FtpTargetTests/SftpTargetTests.

    private static BackupTarget NewTestConfig() => new()
    {
        Type = TargetType.S3,
        Endpoint = Endpoint,
        AccessKey = "minioadmin",
        EncryptedSecretKey = CredentialProtector.Protect("minioadmin"),
        Region = "us-east-1",
        Bucket = $"sparkvault-test-{Guid.NewGuid():N}",
        RemotePath = "backups",
    };

    [Fact]
    public void Constructor_NoEndpoint_DoesNotThrow()
    {
        var target = new S3Target(new BackupTarget
        {
            Type = TargetType.S3,
            Bucket = "b",
            AccessKey = "a",
            Region = "eu-central-1",
            EncryptedSecretKey = CredentialProtector.Protect("s"),
        });
        Assert.NotNull(target);
    }

    [Fact]
    public async Task UploadAsync_UploadsVerifiesAndListsFile()
    {
        if (!DockerTestHelper.IsReachable("127.0.0.1", Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-s3-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            await File.WriteAllTextAsync(filePath, "hello s3");
            var file = new BackupFile(filePath, "a.txt", new FileInfo(filePath).Length);

            await using var target = new S3Target(NewTestConfig());

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
    public async Task UploadAsync_NestedRelativePath_CreatesSubfolderKey()
    {
        if (!DockerTestHelper.IsReachable("127.0.0.1", Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-s3-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "b.txt");
            await File.WriteAllTextAsync(filePath, "nested");
            // RelativePath uses a Windows-style backslash, exactly what FileScanner produces on Windows.
            var file = new BackupFile(filePath, "sub\\b.txt", new FileInfo(filePath).Length);

            await using var target = new S3Target(NewTestConfig());
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
    public async Task UploadAsync_OnPreCancellation_LeavesNoObjectBehind()
    {
        if (!DockerTestHelper.IsReachable("127.0.0.1", Port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-s3-src-");
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "c.txt");
            await File.WriteAllTextAsync(filePath, "cancel me");
            var file = new BackupFile(filePath, "c.txt", new FileInfo(filePath).Length);

            await using var target = new S3Target(NewTestConfig());
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
        if (!DockerTestHelper.IsReachable("127.0.0.1", Port)) return;

        var config = NewTestConfig();
        config.EncryptedSecretKey = CredentialProtector.Protect("wrong-secret");

        await using var target = new S3Target(config);

        Assert.False(await target.TestConnectionAsync(CancellationToken.None));
    }
}
