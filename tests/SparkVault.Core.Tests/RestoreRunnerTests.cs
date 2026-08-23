using Serilog;
using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class RestoreRunnerTests
{
    private static string NewTempDbConnectionString(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"sparkvault-test-{Guid.NewGuid():N}.db");
        return $"Data Source={dbPath}";
    }

    [Fact]
    public async Task RestoreAsync_AfterSourceDeleted_RecreatesOriginalFiles()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-restore-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-restore-dest-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            File.WriteAllText(Path.Combine(srcDir.FullName, "a.txt"), "original content a");
            File.WriteAllText(Path.Combine(srcDir.FullName, "b.txt"), "original content b");

            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "Test",
                SourcePath = srcDir.FullName,
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = destDir.FullName } },
            });
            var job = jobRepo.GetById(jobId)!;
            var targetConfig = job.Targets[0];

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var backupRunner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);
            var results = await backupRunner.RunAsync(job, progress: null, CancellationToken.None);
            var runId = results[0].Id;

            // Simulate data loss: delete one file, corrupt the other.
            File.Delete(Path.Combine(srcDir.FullName, "a.txt"));
            File.WriteAllText(Path.Combine(srcDir.FullName, "b.txt"), "corrupted!");

            var restoreRunner = new RestoreRunner(runFileRepo, new QuarantineRepository(connectionString), new SemaphoreSlim(1, 1), Log.Logger);
            await restoreRunner.RestoreAsync(job, targetConfig, runId, progress: null, CancellationToken.None);

            Assert.Equal("original content a", await File.ReadAllTextAsync(Path.Combine(srcDir.FullName, "a.txt")));
            Assert.Equal("original content b", await File.ReadAllTextAsync(Path.Combine(srcDir.FullName, "b.txt")));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RestoreAsync_WhenDownloadFails_LeavesOriginalFileUnchangedAndNoTempFile()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-restore-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-restore-dest-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            File.WriteAllText(Path.Combine(srcDir.FullName, "a.txt"), "original content a");

            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "Test",
                SourcePath = srcDir.FullName,
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = destDir.FullName } },
            });
            var job = jobRepo.GetById(jobId)!;
            var targetConfig = job.Targets[0];

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var backupRunner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);
            var results = await backupRunner.RunAsync(job, progress: null, CancellationToken.None);
            var runId = results[0].Id;

            // Simulate a mid-flight failure on the target side (e.g. cancelled/interrupted
            // transfer, or the stored copy going missing): delete the backed-up copy from the
            // target so DownloadAsync throws, and corrupt the still-present original so we can
            // tell whether the restore attempt touched it.
            File.Delete(Path.Combine(destDir.FullName, "Test", "a.txt"));
            File.WriteAllText(Path.Combine(srcDir.FullName, "a.txt"), "corrupted-before-restore");

            var restoreRunner = new RestoreRunner(runFileRepo, new QuarantineRepository(connectionString), new SemaphoreSlim(1, 1), Log.Logger);
            await Assert.ThrowsAnyAsync<IOException>(
                () => restoreRunner.RestoreAsync(job, targetConfig, runId, progress: null, CancellationToken.None));

            Assert.Equal("corrupted-before-restore", await File.ReadAllTextAsync(Path.Combine(srcDir.FullName, "a.txt")));
            Assert.False(File.Exists(Path.Combine(srcDir.FullName, "a.txt.sparkvault-tmp")));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RestoreAsync_FtpDownloadFailsAfterCreatingDestination_LeavesOriginalFileUnchanged()
    {
        const string host = "127.0.0.1";
        const int port = 2121;
        if (!DockerTestHelper.IsReachable(host, port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-restore-ftp-src-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            File.WriteAllText(Path.Combine(srcDir.FullName, "a.txt"), "original content a");

            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "Test",
                SourcePath = srcDir.FullName,
                Targets = new List<BackupTarget>
                {
                    new()
                    {
                        Type = TargetType.Ftp,
                        Host = host,
                        Port = port,
                        Username = "testuser",
                        EncryptedPassword = CredentialProtector.Protect("testpass"),
                        EncryptionMode = FtpEncryption.None,
                        RemotePath = $"/test-{Guid.NewGuid():N}",
                    },
                },
            });
            var job = jobRepo.GetById(jobId)!;
            var targetConfig = job.Targets[0];

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var backupRunner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);
            var results = await backupRunner.RunAsync(job, progress: null, CancellationToken.None);
            var runId = results[0].Id;

            // Delete the uploaded copy directly on the FTP server so the restore's download
            // fails. FtpTarget.DownloadAsync does `File.Create(localDestinationPath)` before it
            // knows whether the remote file exists, so on the pre-fix code path (which downloads
            // straight onto the original file) this truncates the user's real file to 0 bytes
            // before the "download failed" exception ever surfaces -- the exact data-loss bug
            // Fix 1 closes, and a scenario the LocalTarget test above can't reproduce because
            // File.Copy never touches a destination it can't fully write.
            var manifestEntry = runFileRepo.GetByRunId(runId).Single();
            await using (var ftp = new FtpTarget(targetConfig))
            {
                await ftp.DeleteAsync(manifestEntry.RelativePath, CancellationToken.None);
            }

            // Overwrite the local file with content that must survive the failed restore.
            File.WriteAllText(Path.Combine(srcDir.FullName, "a.txt"), "must-survive-the-failed-restore");

            var restoreRunner = new RestoreRunner(runFileRepo, new QuarantineRepository(connectionString), new SemaphoreSlim(1, 1), Log.Logger);
            await Assert.ThrowsAnyAsync<Exception>(
                () => restoreRunner.RestoreAsync(job, targetConfig, runId, progress: null, CancellationToken.None));

            Assert.Equal("must-survive-the-failed-restore", await File.ReadAllTextAsync(Path.Combine(srcDir.FullName, "a.txt")));
            Assert.False(File.Exists(Path.Combine(srcDir.FullName, "a.txt.sparkvault-tmp")));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RestoreAsync_AfterJobRename_RestoresToOriginalRelativePaths()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-restore-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-restore-dest-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            File.WriteAllText(Path.Combine(srcDir.FullName, "a.txt"), "original content a");
            Directory.CreateDirectory(Path.Combine(srcDir.FullName, "sub"));
            File.WriteAllText(Path.Combine(srcDir.FullName, "sub", "b.txt"), "original content b");

            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "OldName",
                SourcePath = srcDir.FullName,
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = destDir.FullName } },
            });
            var job = jobRepo.GetById(jobId)!;
            var targetConfig = job.Targets[0];

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var backupRunner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);
            var results = await backupRunner.RunAsync(job, progress: null, CancellationToken.None);
            var runId = results[0].Id;

            // Rename the job after the backup ran under the old name, then wipe the source so
            // the restore has to recreate the files. The manifest still says "OldName\...".
            job.Name = "NewName";
            jobRepo.Update(job);
            File.Delete(Path.Combine(srcDir.FullName, "a.txt"));
            File.Delete(Path.Combine(srcDir.FullName, "sub", "b.txt"));

            var restoreRunner = new RestoreRunner(runFileRepo, new QuarantineRepository(connectionString), new SemaphoreSlim(1, 1), Log.Logger);
            await restoreRunner.RestoreAsync(job, targetConfig, runId, progress: null, CancellationToken.None);

            Assert.Equal("original content a", await File.ReadAllTextAsync(Path.Combine(srcDir.FullName, "a.txt")));
            Assert.Equal("original content b", await File.ReadAllTextAsync(Path.Combine(srcDir.FullName, "sub", "b.txt")));
            Assert.False(Directory.Exists(Path.Combine(srcDir.FullName, "OldName")));
            Assert.False(Directory.Exists(Path.Combine(srcDir.FullName, "NewName")));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RestoreAsync_FileQuarantinedByLaterRun_StillRestoresFromQuarantine()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-restore-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-restore-dest-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            File.WriteAllText(filePath, "will be deleted later");

            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "Test",
                SourcePath = srcDir.FullName,
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = destDir.FullName } },
            });
            var job = jobRepo.GetById(jobId)!;
            var targetConfig = job.Targets[0];

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var backupRunner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            // Run 1: file A gets backed up.
            var firstResults = await backupRunner.RunAsync(job, progress: null, CancellationToken.None);
            var firstRunId = firstResults[0].Id;

            // Delete from source, run again: run 2 quarantines A on the target.
            File.Delete(filePath);
            await backupRunner.RunAsync(job, progress: null, CancellationToken.None);

            // Restoring run 1 (whose catalog still says "a.txt" at its original path) must fall
            // back to wherever run 2's quarantine moved it.
            var restoreRunner = new RestoreRunner(runFileRepo, quarantineRepo, new SemaphoreSlim(1, 1), Log.Logger);
            await restoreRunner.RestoreAsync(job, targetConfig, firstRunId, progress: null, CancellationToken.None);

            Assert.Equal("will be deleted later", await File.ReadAllTextAsync(filePath));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
