using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class RunRepositoryTests
{
    private static string NewTempDbConnectionString(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"sparkvault-test-{Guid.NewGuid():N}.db");
        return $"Data Source={dbPath}";
    }

    [Fact]
    public void AddThenUpdate_PersistsCompletion()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob { Name = "A", SourcePath = "C:\\a" });

            var runRepo = new RunRepository(connectionString);
            var run = new BackupRun { JobId = jobId, StartedAt = new DateTime(2026, 8, 19, 10, 0, 0), Status = RunStatus.Failed };
            var runId = run.Id = runRepo.Add(run);

            run.Status = RunStatus.Success;
            run.EndedAt = new DateTime(2026, 8, 19, 10, 5, 0);
            run.FileCount = 3;
            run.TotalBytes = 1024;
            runRepo.Update(run);

            var loaded = runRepo.GetByJobId(jobId).Single(r => r.Id == runId);
            Assert.Equal(RunStatus.Success, loaded.Status);
            Assert.Equal(3, loaded.FileCount);
            Assert.Equal(1024, loaded.TotalBytes);
            Assert.Equal(new DateTime(2026, 8, 19, 10, 5, 0), loaded.EndedAt);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetLatestByJobId_ReturnsMostRecentStart()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob { Name = "A", SourcePath = "C:\\a" });

            var runRepo = new RunRepository(connectionString);
            runRepo.Add(new BackupRun { JobId = jobId, StartedAt = new DateTime(2026, 8, 18, 10, 0, 0), Status = RunStatus.Success });
            runRepo.Add(new BackupRun { JobId = jobId, StartedAt = new DateTime(2026, 8, 19, 10, 0, 0), Status = RunStatus.Success });

            var latest = runRepo.GetLatestByJobId(jobId);

            Assert.NotNull(latest);
            Assert.Equal(new DateTime(2026, 8, 19, 10, 0, 0), latest!.StartedAt);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void RoundTrip_PreservesUtcKindAndInstant()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob { Name = "A", SourcePath = "C:\\a" });

            var startedAt = DateTime.UtcNow;
            var runRepo = new RunRepository(connectionString);
            var run = new BackupRun { JobId = jobId, StartedAt = startedAt, Status = RunStatus.Success };
            run.Id = runRepo.Add(run);
            run.EndedAt = startedAt.AddMinutes(1);
            runRepo.Update(run);

            var loaded = runRepo.GetLatestByJobId(jobId)!;

            Assert.Equal(DateTimeKind.Utc, loaded.StartedAt.Kind);
            Assert.Equal(DateTimeKind.Utc, loaded.EndedAt!.Value.Kind);
            Assert.Equal(startedAt, loaded.StartedAt, TimeSpan.FromSeconds(1));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetLatestByJobId_ReturnsNullWhenNoRuns()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var runRepo = new RunRepository(connectionString);

            Assert.Null(runRepo.GetLatestByJobId(999));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
