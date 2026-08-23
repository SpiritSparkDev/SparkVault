using Serilog;
using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class OnChangeJobCheckerTests
{
    private static string NewTempDbConnectionString(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"sparkvault-test-{Guid.NewGuid():N}.db");
        return $"Data Source={dbPath}";
    }

    [Fact]
    public async Task RunDueJobsAsync_NeverRunBefore_RunsIt()
    {
        var srcDir = Directory.CreateTempSubdirectory("sparkvault-src-");
        var destDir = Directory.CreateTempSubdirectory("sparkvault-dest-");
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            File.WriteAllText(Path.Combine(srcDir.FullName, "a.txt"), "hello");

            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            jobRepo.Add(new BackupJob
            {
                Name = "OnChangeJob",
                SourcePath = srcDir.FullName,
                ScheduleType = ScheduleType.OnChange,
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = destDir.FullName } },
            });

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            await OnChangeJobChecker.RunDueJobsAsync(jobRepo, runRepo, runFileRepo, runner, Log.Logger, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(destDir.FullName, "OnChangeJob", "a.txt")));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunDueJobsAsync_NothingChangedSinceLastRun_DoesNotRun()
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
                Name = "OnChangeJob",
                SourcePath = srcDir.FullName,
                ScheduleType = ScheduleType.OnChange,
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = destDir.FullName } },
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            // Pre-existing successful run with an up-to-date catalog, as if a manual run already happened.
            await runner.RunAsync(job, progress: null, CancellationToken.None);
            var runCountBefore = runRepo.GetByJobId(jobId).Count;

            await OnChangeJobChecker.RunDueJobsAsync(jobRepo, runRepo, runFileRepo, runner, Log.Logger, CancellationToken.None);

            Assert.Equal(runCountBefore, runRepo.GetByJobId(jobId).Count);
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunDueJobsAsync_FileChangedSinceLastRun_RunsAgain()
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
                Name = "OnChangeJob",
                SourcePath = srcDir.FullName,
                ScheduleType = ScheduleType.OnChange,
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = destDir.FullName } },
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            await runner.RunAsync(job, progress: null, CancellationToken.None);
            var runCountBefore = runRepo.GetByJobId(jobId).Count;

            await Task.Delay(50);
            File.WriteAllText(filePath, "changed content, much longer than before");

            await OnChangeJobChecker.RunDueJobsAsync(jobRepo, runRepo, runFileRepo, runner, Log.Logger, CancellationToken.None);

            Assert.Equal(runCountBefore + 1, runRepo.GetByJobId(jobId).Count);
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunDueJobsAsync_JobWithoutTargets_NeverRuns()
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
                Name = "NoTargets",
                SourcePath = srcDir.FullName,
                ScheduleType = ScheduleType.OnChange,
                Targets = new List<BackupTarget>(),
            });

            var runRepo = new RunRepository(connectionString);
            var runFileRepo = new RunFileRepository(connectionString);
            var quarantineRepo = new QuarantineRepository(connectionString);
            var runner = new BackupRunner(runRepo, runFileRepo, quarantineRepo, Log.Logger);

            await OnChangeJobChecker.RunDueJobsAsync(jobRepo, runRepo, runFileRepo, runner, Log.Logger, CancellationToken.None);

            Assert.Empty(runRepo.GetByJobId(jobId));
        }
        finally
        {
            srcDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
