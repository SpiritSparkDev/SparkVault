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
    public async Task RunAsync_CopiesFilesAndRecordsSuccessfulRun()
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
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runner = new BackupRunner(runRepo, Log.Logger);

            var run = await runner.RunAsync(job, new LocalTarget(destDir.FullName), progress: null, CancellationToken.None);

            Assert.Equal(RunStatus.Success, run.Status);
            Assert.Equal(2, run.FileCount);
            Assert.Equal(11, run.TotalBytes);
            Assert.True(File.Exists(Path.Combine(destDir.FullName, "a.txt")));
            Assert.True(File.Exists(Path.Combine(destDir.FullName, "b.txt")));

            var persisted = runRepo.GetByJobId(jobId).Single();
            Assert.Equal(RunStatus.Success, persisted.Status);
            Assert.NotNull(persisted.EndedAt);
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task RunAsync_OnSourceMissing_RecordsFailedRun()
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
            });
            var job = jobRepo.GetById(jobId)!;

            var runRepo = new RunRepository(connectionString);
            var runner = new BackupRunner(runRepo, Log.Logger);

            var run = await runner.RunAsync(job, new LocalTarget(destDir.FullName), progress: null, CancellationToken.None);

            Assert.Equal(RunStatus.Failed, run.Status);
            Assert.NotNull(run.ErrorMessage);
        }
        finally
        {
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
