using Serilog;
using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class BackgroundSchedulerTests
{
    private static string NewTempDbConnectionString(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"sparkvault-test-{Guid.NewGuid():N}.db");
        return $"Data Source={dbPath}";
    }

    [Fact]
    public async Task RunsDueJobOnFirstTick()
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
                Name = "Scheduled",
                SourcePath = srcDir.FullName,
                ScheduleType = ScheduleType.Interval,
                IntervalHours = 6,
                // ponytail: temporary bridge (Task 12) — BackupRunner now runs job.Targets, not the
                // targetFactory below; superseded wholesale by Task 13's rewrite of this test file.
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = destDir.FullName } },
            });

            var runRepo = new RunRepository(connectionString);
            var runner = new BackupRunner(runRepo, Log.Logger);

            await using var scheduler = new BackgroundScheduler(
                jobRepo, runRepo, runner,
                job => new LocalTarget(destDir.FullName),
                pollInterval: TimeSpan.FromMilliseconds(50),
                Log.Logger);

            await Task.Delay(TimeSpan.FromMilliseconds(400));

            var runs = runRepo.GetByJobId(jobId);
            Assert.NotEmpty(runs);
            Assert.Equal(RunStatus.Success, runs[0].Status);
        }
        finally
        {
            srcDir.Delete(recursive: true);
            destDir.Delete(recursive: true);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
