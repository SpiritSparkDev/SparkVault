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
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            var results = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Single(results);
            Assert.Equal(RunStatus.Success, results[0].Status);
            Assert.Equal(2, results[0].FileCount);
            Assert.Equal(11, results[0].TotalBytes);
            Assert.True(File.Exists(Path.Combine(destDir.FullName, "Test", "a.txt")));
            Assert.True(File.Exists(Path.Combine(destDir.FullName, "Test", "b.txt")));
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
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            var results = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.Equal(RunStatus.Success, r.Status));
            var groupIds = results.Select(r => r.RunGroupId).Distinct().ToList();
            Assert.Single(groupIds);
            var targetIds = results.Select(r => r.TargetId).Distinct().ToList();
            Assert.Equal(2, targetIds.Count);
            Assert.True(File.Exists(Path.Combine(destDir1.FullName, "Test", "a.txt")));
            Assert.True(File.Exists(Path.Combine(destDir2.FullName, "Test", "a.txt")));
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
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            var results = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Equal(2, results.Count);
            Assert.Equal(RunStatus.Failed, results[0].Status);
            Assert.Equal(RunStatus.Success, results[1].Status);
            Assert.True(File.Exists(Path.Combine(destDirOk.FullName, "Test", "a.txt")));
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
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

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
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

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
        // Now reported twice per file (before and after upload) — only the after-upload report
        // (FilesDone > 0) matches "cancels once the first target finishes its first file".
        public void Report(TransferProgress value)
        {
            if (value.FilesDone > 0) _cts.Cancel();
        }
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
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

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
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            var results = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Equal(3, results.Count);
            Assert.All(results, r => Assert.Equal(RunStatus.Success, r.Status));
            Assert.True(File.Exists(Path.Combine(destDir.FullName, "Test", "a.txt")));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_AllFourTargetTypesTogether_AllSucceed()
    {
        const string host = "127.0.0.1";
        if (!DockerTestHelper.IsReachable(host, 2121) || !DockerTestHelper.IsReachable(host, 2222) || !DockerTestHelper.IsReachable(host, 9000)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        var suffix = Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(Path.Combine(srcDir.FullName, "a.txt"), "hello");
            // S3Target no longer creates buckets itself — simulate the user having already
            // created it with their provider.
            await S3TargetTests.CreateBucketAsync($"four-{suffix}");

            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "Test",
                SourcePath = srcDir.FullName,
                Targets = new List<BackupTarget>
                {
                    new() { Type = TargetType.Local, DestinationPath = destDir.FullName },
                    new() { Type = TargetType.Ftp, Host = host, Port = 2121, Username = "testuser", EncryptedPassword = CredentialProtector.Protect("testpass"), EncryptionMode = FtpEncryption.None, RemotePath = $"/four-{suffix}" },
                    new() { Type = TargetType.Sftp, Host = host, Port = 2222, Username = "testuser", EncryptedPassword = CredentialProtector.Protect("testpass"), RemotePath = $"/upload/four-{suffix}" },
                    new() { Type = TargetType.S3, Endpoint = "http://127.0.0.1:9000", AccessKey = "minioadmin", EncryptedSecretKey = CredentialProtector.Protect("minioadmin"), Region = "us-east-1", Bucket = $"four-{suffix}", RemotePath = "" },
                },
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            var results = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Equal(4, results.Count);
            Assert.All(results, r => Assert.Equal(RunStatus.Success, r.Status));
            Assert.True(File.Exists(Path.Combine(destDir.FullName, "Test", "a.txt")));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_PausedBetweenFiles_WaitsForResumeThenFinishes()
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
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            // Paused before the run starts (not from a Progress<T> callback — Progress.Report
            // marshals to the captured context asynchronously, so a Pause() called from inside
            // one races the loop instead of reliably blocking it).
            var pauseToken = new PauseToken();
            pauseToken.Pause();
            _ = Task.Delay(150).ContinueWith(_ => pauseToken.Resume());

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var results = await runner.RunAsync(job, progress: null, CancellationToken.None, pauseToken);
            sw.Stop();

            Assert.Single(results);
            Assert.Equal(RunStatus.Success, results[0].Status);
            Assert.Equal(2, results[0].FileCount);
            Assert.True(sw.ElapsedMilliseconds >= 140, $"Expected the run to be delayed by the pause, took {sw.ElapsedMilliseconds}ms");
            Assert.True(File.Exists(Path.Combine(destDir.FullName, "Test", "a.txt")));
            Assert.True(File.Exists(Path.Combine(destDir.FullName, "Test", "b.txt")));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_SuccessfulRun_WritesRunFilesManifest()
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
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            var results = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Equal(RunStatus.Success, results[0].Status);
            var manifest = runFileRepo.GetByRunId(results[0].Id);
            Assert.Equal(2, manifest.Count);
            Assert.Contains(manifest, f => f.RelativePath == "Test\\a.txt" && f.Size == 5);
            Assert.Contains(manifest, f => f.RelativePath == "Test\\b.txt" && f.Size == 6);
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_FailedRun_WritesNoRunFilesManifest()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
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
                // Unreachable FTP target -> TestConnectionAsync fails -> run is Failed, no files uploaded.
                Targets = new List<BackupTarget> { new() { Type = TargetType.Ftp, Host = "127.0.0.1", Port = 1, Username = "x", RemotePath = "/x" } },
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            var results = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Equal(RunStatus.Failed, results[0].Status);
            Assert.Empty(runFileRepo.GetByRunId(results[0].Id));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_SecondRunWithNoChanges_TransfersNothingButKeepsFullManifest()
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
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            var firstResults = await runner.RunAsync(job, progress: null, CancellationToken.None);
            var secondResults = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Equal(0, secondResults[0].FileCount);
            Assert.Equal(0, secondResults[0].TotalBytes);
            var manifest = runFileRepo.GetByRunId(secondResults[0].Id);
            Assert.Equal(2, manifest.Count);
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_ChangedFile_IsReUploaded()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            File.WriteAllText(filePath, "hello");

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
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            await runner.RunAsync(job, progress: null, CancellationToken.None);

            // Ensure a strictly later, distinguishable LastWriteTimeUtc than the first write.
            await Task.Delay(50);
            File.WriteAllText(filePath, "hello there, much longer content now");

            var secondResults = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Equal(1, secondResults[0].FileCount);
            Assert.Equal("hello there, much longer content now".Length, secondResults[0].TotalBytes);
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_DeletedSourceFile_IsQuarantinedAndDroppedFromNewManifest()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            var filePath = Path.Combine(srcDir.FullName, "a.txt");
            File.WriteAllText(filePath, "hello");

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
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            await runner.RunAsync(job, progress: null, CancellationToken.None);
            File.Delete(filePath);

            var secondResults = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Empty(runFileRepo.GetByRunId(secondResults[0].Id));
            Assert.False(File.Exists(Path.Combine(destDir.FullName, "Test", "a.txt")));
            Assert.True(Directory.Exists(Path.Combine(destDir.FullName, "_deleted")));
            var quarantinePath = quarantineRepo.GetLatestQuarantinePath(jobId, job.Targets[0].Id, "Test\\a.txt");
            Assert.NotNull(quarantinePath);
            Assert.True(File.Exists(Path.Combine(destDir.FullName, quarantinePath!)));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_RetentionDaysSet_PurgesQuarantineEntryOlderThanCutoff()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
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
                RetentionDays = 1,
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = destDir.FullName } },
            });
            var job = jobRepo.GetById(jobId)!;
            var targetId = job.Targets[0].Id;

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            await runner.RunAsync(job, progress: null, CancellationToken.None);

            // Simulate a file that was quarantined 10 days ago by an earlier run — well past
            // this job's 1-day retention window — by placing the file directly and registering
            // it, rather than waiting on real wall-clock time to age an entry.
            var oldQuarantinePath = "_deleted\\old\\Test\\old.txt";
            var oldQuarantineFullPath = Path.Combine(destDir.FullName, oldQuarantinePath);
            Directory.CreateDirectory(Path.GetDirectoryName(oldQuarantineFullPath)!);
            File.WriteAllText(oldQuarantineFullPath, "old quarantined content");
            quarantineRepo.Add(jobId, targetId, "Test\\old.txt", oldQuarantinePath, quarantinedAtRunId: 1, quarantinedAtUtc: DateTime.UtcNow.AddDays(-10));

            await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.False(File.Exists(oldQuarantineFullPath));
            Assert.Null(quarantineRepo.GetLatestQuarantinePath(jobId, targetId, "Test\\old.txt"));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_RetentionDaysNotSet_LeavesOldQuarantineEntriesUntouched()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
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
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = destDir.FullName } },
            });
            var job = jobRepo.GetById(jobId)!;
            var targetId = job.Targets[0].Id;

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            await runner.RunAsync(job, progress: null, CancellationToken.None);

            var oldQuarantinePath = "_deleted\\old\\Test\\old.txt";
            var oldQuarantineFullPath = Path.Combine(destDir.FullName, oldQuarantinePath);
            Directory.CreateDirectory(Path.GetDirectoryName(oldQuarantineFullPath)!);
            File.WriteAllText(oldQuarantineFullPath, "old quarantined content");
            quarantineRepo.Add(jobId, targetId, "Test\\old.txt", oldQuarantinePath, quarantinedAtRunId: 1, quarantinedAtUtc: DateTime.UtcNow.AddDays(-10));

            await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.True(File.Exists(oldQuarantineFullPath));
            Assert.NotNull(quarantineRepo.GetLatestQuarantinePath(jobId, targetId, "Test\\old.txt"));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_VerifyTargetBeforeRunOnSftp_ReUploadsOnlyTheFileMissingOnTheTarget()
    {
        const string host = "127.0.0.1";
        const int port = 2222;
        if (!DockerTestHelper.IsReachable(host, port)) return;

        var srcDir = Directory.CreateTempSubdirectory("sparkvault-verify-src-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            File.WriteAllText(Path.Combine(srcDir.FullName, "keep.txt"), "still on the target");
            File.WriteAllText(Path.Combine(srcDir.FullName, "drift.txt"), "deleted on the target");

            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "Test",
                SourcePath = srcDir.FullName,
                VerifyTargetBeforeRun = true,
                Targets = new List<BackupTarget>
                {
                    new()
                    {
                        Type = TargetType.Sftp, Host = host, Port = port, Username = "testuser",
                        EncryptedPassword = CredentialProtector.Protect("testpass"),
                        RemotePath = $"/upload/verify-{Guid.NewGuid():N}",
                    },
                },
            });
            var job = jobRepo.GetById(jobId)!;
            var targetConfig = job.Targets[0];

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            await runner.RunAsync(job, progress: null, CancellationToken.None);

            // External drift: one file disappears from the target behind the app's back. SFTP
            // reports its paths with forward slashes while the manifest stores backslashes — if
            // the verification filter doesn't normalize both sides it discards the whole manifest
            // and re-uploads everything, including the file that is still perfectly fine.
            await using (var sftp = new SftpTarget(targetConfig))
            {
                await sftp.DeleteAsync("Test\\drift.txt", CancellationToken.None);
            }

            var secondResults = await runner.RunAsync(job, progress: null, CancellationToken.None);

            Assert.Equal(1, secondResults[0].FileCount);
            Assert.Equal("deleted on the target".Length, secondResults[0].TotalBytes);
            Assert.Equal(2, runFileRepo.GetByRunId(secondResults[0].Id).Count);
        }
        finally
        {
            srcDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
