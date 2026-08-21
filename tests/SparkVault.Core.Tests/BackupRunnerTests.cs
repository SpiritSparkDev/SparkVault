using Serilog;
using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class BackupRunnerTests
{
    private static string NewTempDbConnectionString(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"sparkvault-test-{Guid.NewGuid():N}.db");
        return $"Data Source={dbPath}";
    }

    [Fact]
    public async Task RunAsync_SingleTarget_CopiesFilesAndRecordsSuccessfulRun()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            File.WriteAllText(Path.Combine(srcDir.FullName, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(srcDir.FullName, "b.txt"), "world!");

            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "Test",
                SourcePath = srcDir.FullName,
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = destDir.FullName } },
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runner = new BackupRunner(runRepo, Log.Logger);

            var results = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Single(results);
            Assert.Equal(RunStatus.Success, results[0].Status);
            Assert.Equal(2, results[0].FileCount);
            Assert.Equal(11, results[0].TotalBytes);
            Assert.True(File.Exists(Path.Combine(destDir.FullName, "a.txt")));
            Assert.True(File.Exists(Path.Combine(destDir.FullName, "b.txt")));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_MultipleTargets_RecordsOneRunPerTargetWithSharedGroupId()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir1 = Directory.CreateTempSubdirectory("sparkvault-dest1-");
        var destDir2 = Directory.CreateTempSubdirectory("sparkvault-dest2-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            File.WriteAllText(Path.Combine(srcDir.FullName, "a.txt"), "hello");

            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "Test",
                SourcePath = srcDir.FullName,
                Targets = new List<BackupTarget>
                {
                    new() { Type = TargetType.Local, DestinationPath = destDir1.FullName },
                    new() { Type = TargetType.Local, DestinationPath = destDir2.FullName },
                },
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runner = new BackupRunner(runRepo, Log.Logger);

            var results = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.Equal(RunStatus.Success, r.Status));
            var groupIds = results.Select(r => r.RunGroupId).Distinct().ToList();
            Assert.Single(groupIds);
            var targetIds = results.Select(r => r.TargetId).Distinct().ToList();
            Assert.Equal(2, targetIds.Count);
            Assert.True(File.Exists(Path.Combine(destDir1.FullName, "a.txt")));
            Assert.True(File.Exists(Path.Combine(destDir2.FullName, "a.txt")));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir1.Delete(recursive: true);
            destDir2.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_OneTargetFails_OtherTargetStillAttempted()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDirOk = Directory.CreateTempSubdirectory("sparkvault-dest-ok-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            File.WriteAllText(Path.Combine(srcDir.FullName, "a.txt"), "hello");

            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "Test",
                SourcePath = srcDir.FullName,
                Targets = new List<BackupTarget>
                {
                    // Unreachable FTP host — TestConnectionAsync will fail fast.
                    new() { Type = TargetType.Ftp, Host = "127.0.0.1", Port = 1, Username = "x", EncryptedPassword = CredentialProtector.Protect("x"), RemotePath = "/x" },
                    new() { Type = TargetType.Local, DestinationPath = destDirOk.FullName },
                },
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runner = new BackupRunner(runRepo, Log.Logger);

            var results = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Equal(2, results.Count);
            Assert.Equal(RunStatus.Failed, results[0].Status);
            Assert.Equal(RunStatus.Success, results[1].Status);
            Assert.True(File.Exists(Path.Combine(destDirOk.FullName, "a.txt")));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDirOk.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_OnSourceMissing_RecordsFailedRunForEveryTarget()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "Test",
                SourcePath = Path.Combine(Path.GetTempPath(), "sparkvault-does-not-exist"),
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = destDir.FullName } },
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runner = new BackupRunner(runRepo, Log.Logger);

            var results = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Single(results);
            Assert.Equal(RunStatus.Failed, results[0].Status);
            Assert.NotNull(results[0].ErrorMessage);
        }
        finally
        {
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_FiresRunStartedOnceAndRunCompletedOnceWithAggregatedStatus()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir1 = Directory.CreateTempSubdirectory("sparkvault-dest1-");
        var destDir2 = Directory.CreateTempSubdirectory("sparkvault-dest2-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            File.WriteAllText(Path.Combine(srcDir.FullName, "a.txt"), "hello");

            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "Test",
                SourcePath = srcDir.FullName,
                Targets = new List<BackupTarget>
                {
                    new() { Type = TargetType.Local, DestinationPath = destDir1.FullName },
                    new() { Type = TargetType.Local, DestinationPath = destDir2.FullName },
                },
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runner = new BackupRunner(runRepo, Log.Logger);

            var startedCount = 0;
            var completedStatuses = new List<RunStatus>();
            runner.RunStarted += _ => startedCount++;
            runner.RunCompleted += (_, status) => completedStatuses.Add(status);

            await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Equal(1, startedCount);
            Assert.Equal(new[] { RunStatus.Success }, completedStatuses);
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir1.Delete(recursive: true);
            destDir2.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    // A pre-cancelled token can't exercise the per-target branch: RunAsync's very first await is
    // _runLock.WaitAsync(ct), which throws before any target is reached. Cancelling from the
    // progress callback instead reproduces the real case — one target in flight (app quit
    // mid-run), the rest never started.
    private sealed class CancelOnFirstReport : IProgress<TransferProgress>
    {
        private readonly CancellationTokenSource _cts;
        public CancelOnFirstReport(CancellationTokenSource cts) => _cts = cts;
        public void Report(TransferProgress value) => _cts.Cancel();
    }

    [Fact]
    public async Task RunAsync_CancelledMidRun_RecordsNotYetStartedTargetsAsCancelled()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDirs = Enumerable.Range(1, 3)
            .Select(i => Directory.CreateTempSubdirectory($"sparkvault-dest{i}-")).ToList();
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            File.WriteAllText(Path.Combine(srcDir.FullName, "a.txt"), "hello");

            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "Test",
                SourcePath = srcDir.FullName,
                Targets = destDirs
                    .Select(d => new BackupTarget { Type = TargetType.Local, DestinationPath = d.FullName })
                    .ToList(),
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runner = new BackupRunner(runRepo, Log.Logger);

            using var cts = new CancellationTokenSource();

            // Cancels as soon as the first target finishes its first file, so targets 2 and 3
            // never start — they must be recorded Cancelled, not Failed.
            var results = await runner.RunAsync(job, new CancelOnFirstReport(cts), cts.Token);

            Assert.Equal(3, results.Count);
            Assert.Equal(RunStatus.Success, results[0].Status);
            Assert.Equal(RunStatus.Cancelled, results[1].Status);
            Assert.Equal(RunStatus.Cancelled, results[2].Status);
            Assert.All(results, r => Assert.Null(r.ErrorMessage));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            foreach (var d in destDirs) d.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_LocalFtpAndSftpTargetsTogether_AllSucceed()
    {
        const string host = "127.0.0.1";
        if (!DockerTestHelper.IsReachable(host, 2121) || !DockerTestHelper.IsReachable(host, 2222)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        var suffix = Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(Path.Combine(srcDir.FullName, "a.txt"), "hello");

            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "Test",
                SourcePath = srcDir.FullName,
                Targets = new List<BackupTarget>
                {
                    new() { Type = TargetType.Local, DestinationPath = destDir.FullName },
                    new()
                    {
                        Type = TargetType.Ftp, Host = host, Port = 2121, Username = "testuser",
                        EncryptedPassword = CredentialProtector.Protect("testpass"),
                        EncryptionMode = FtpEncryption.None,
                        RemotePath = $"/multi-{suffix}",
                    },
                    new()
                    {
                        Type = TargetType.Sftp, Host = host, Port = 2222, Username = "testuser",
                        EncryptedPassword = CredentialProtector.Protect("testpass"),
                        RemotePath = $"/upload/multi-{suffix}",
                    },
                },
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runner = new BackupRunner(runRepo, Log.Logger);

            var results = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Equal(3, results.Count);
            Assert.All(results, r => Assert.Equal(RunStatus.Success, r.Status));
            Assert.True(File.Exists(Path.Combine(destDir.FullName, "a.txt")));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
