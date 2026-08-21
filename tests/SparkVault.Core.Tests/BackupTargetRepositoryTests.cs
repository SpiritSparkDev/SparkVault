using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class BackupTargetRepositoryTests
{
    private static string NewTempDbConnectionString(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"sparkvault-test-{Guid.NewGuid():N}.db");
        return $"Data Source={dbPath}";
    }

    [Fact]
    public void AddThenGetByJobId_RoundTripsLocalTarget()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new BackupTargetRepository(connectionString);

            var target = new BackupTarget { JobId = 1, Type = TargetType.Local, DestinationPath = @"D:\Backups\Docs" };
            var id = repo.Add(target);

            var loaded = repo.GetByJobId(1).Single();
            Assert.Equal(id, loaded.Id);
            Assert.Equal(TargetType.Local, loaded.Type);
            Assert.Equal(@"D:\Backups\Docs", loaded.DestinationPath);
            Assert.Null(loaded.Host);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void AddThenGetByJobId_RoundTripsFtpTargetAllFields()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new BackupTargetRepository(connectionString);

            var target = new BackupTarget
            {
                JobId = 2,
                Type = TargetType.Ftp,
                Host = "ftp.example.com",
                Port = 21,
                Username = "user1",
                EncryptedPassword = "cGxhaW50ZXh0",
                RemotePath = "/backups",
                EncryptionMode = FtpEncryption.Explicit,
            };
            repo.Add(target);

            var loaded = repo.GetByJobId(2).Single();
            Assert.Equal(TargetType.Ftp, loaded.Type);
            Assert.Equal("ftp.example.com", loaded.Host);
            Assert.Equal(21, loaded.Port);
            Assert.Equal("user1", loaded.Username);
            Assert.Equal("cGxhaW50ZXh0", loaded.EncryptedPassword);
            Assert.Equal("/backups", loaded.RemotePath);
            Assert.Equal(FtpEncryption.Explicit, loaded.EncryptionMode);
            Assert.Null(loaded.DestinationPath);
            Assert.Null(loaded.PrivateKeyPath);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void AddThenGetByJobId_RoundTripsSftpTargetWithKeyAuth()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new BackupTargetRepository(connectionString);

            var target = new BackupTarget
            {
                JobId = 3,
                Type = TargetType.Sftp,
                Host = "sftp.example.com",
                Port = 22,
                Username = "user2",
                PrivateKeyPath = @"C:\keys\id_rsa",
                EncryptedKeyPassphrase = "c2VjcmV0",
                RemotePath = "/upload",
            };
            repo.Add(target);

            var loaded = repo.GetByJobId(3).Single();
            Assert.Equal(TargetType.Sftp, loaded.Type);
            Assert.Equal(@"C:\keys\id_rsa", loaded.PrivateKeyPath);
            Assert.Equal("c2VjcmV0", loaded.EncryptedKeyPassphrase);
            Assert.Null(loaded.EncryptionMode);
            Assert.Null(loaded.EncryptedPassword);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void Update_PersistsChanges()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new BackupTargetRepository(connectionString);
            var id = repo.Add(new BackupTarget { JobId = 1, Type = TargetType.Local, DestinationPath = "C:\\a" });

            var target = repo.GetByJobId(1).Single();
            target.DestinationPath = "C:\\b";
            repo.Update(target);

            Assert.Equal("C:\\b", repo.GetByJobId(1).Single().DestinationPath);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void Delete_RemovesTarget()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new BackupTargetRepository(connectionString);
            var id = repo.Add(new BackupTarget { JobId = 1, Type = TargetType.Local, DestinationPath = "C:\\a" });

            repo.Delete(id);

            Assert.Empty(repo.GetByJobId(1));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetByJobId_OnlyReturnsTargetsForThatJob()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new BackupTargetRepository(connectionString);
            repo.Add(new BackupTarget { JobId = 1, Type = TargetType.Local, DestinationPath = "C:\\a" });
            repo.Add(new BackupTarget { JobId = 2, Type = TargetType.Local, DestinationPath = "C:\\b" });

            Assert.Single(repo.GetByJobId(1));
            Assert.Single(repo.GetByJobId(2));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void AddThenGetByJobId_RoundTripsS3TargetAllFields()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new BackupTargetRepository(connectionString);

            var target = new BackupTarget
            {
                JobId = 4,
                Type = TargetType.S3,
                Endpoint = "http://127.0.0.1:9000",
                AccessKey = "minioadmin",
                EncryptedSecretKey = "ZW5jcnlwdGVk",
                Region = "us-east-1",
                Bucket = "sparkvault",
                RemotePath = "backups/job1",
            };
            repo.Add(target);

            var loaded = repo.GetByJobId(4).Single();
            Assert.Equal(TargetType.S3, loaded.Type);
            Assert.Equal("http://127.0.0.1:9000", loaded.Endpoint);
            Assert.Equal("minioadmin", loaded.AccessKey);
            Assert.Equal("ZW5jcnlwdGVk", loaded.EncryptedSecretKey);
            Assert.Equal("us-east-1", loaded.Region);
            Assert.Equal("sparkvault", loaded.Bucket);
            Assert.Equal("backups/job1", loaded.RemotePath);
            Assert.Null(loaded.Host);
            Assert.Null(loaded.DestinationPath);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
