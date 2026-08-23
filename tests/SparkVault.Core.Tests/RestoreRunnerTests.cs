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
            var backupRunner = new BackupRunner(runRepo, runFileRepo, Log.Logger);
            var results = await backupRunner.RunAsync(job, progress: null, CancellationToken.None);
            var runId = results[0].Id;

            // Simulate data loss: delete one file, corrupt the other.
            File.Delete(Path.Combine(srcDir.FullName, "a.txt"));
            File.WriteAllText(Path.Combine(srcDir.FullName, "b.txt"), "corrupted!");

            var restoreRunner = new RestoreRunner(runFileRepo, Log.Logger);
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
}
