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
    public void AddThenUpdate_PersistsCompletionIncludingTargetAndGroup()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob { Name = "A", SourcePath = "C:\\a", Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = "D:\\a" } } });
            var targetId = jobRepo.GetById(jobId)!.Targets.Single().Id;

            var runRepo = new RunRepository(connectionString);
            var groupId = Guid.NewGuid();
            var run = new BackupRun { JobId = jobId, TargetId = targetId, RunGroupId = groupId, StartedAt = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc), Status = RunStatus.Failed };
            var runId = run.Id = runRepo.Add(run);

            run.Status = RunStatus.Success;
            run.EndedAt = new DateTime(2026, 8, 20, 10, 5, 0, DateTimeKind.Utc);
            run.FileCount = 3;
            run.TotalBytes = 1024;
            runRepo.Update(run);

            var loaded = runRepo.GetByJobId(jobId).Single(r => r.Id == runId);
            Assert.Equal(targetId, loaded.TargetId);
            Assert.Equal(groupId, loaded.RunGroupId);
            Assert.Equal(RunStatus.Success, loaded.Status);
            Assert.Equal(3, loaded.FileCount);
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
            var jobId = jobRepo.Add(new BackupJob { Name = "A", SourcePath = "C:\\a", Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = "D:\\a" } } });
            var targetId = jobRepo.GetById(jobId)!.Targets.Single().Id;

            var runRepo = new RunRepository(connectionString);
            runRepo.Add(new BackupRun { JobId = jobId, TargetId = targetId, RunGroupId = Guid.NewGuid(), StartedAt = new DateTime(2026, 8, 19, 10, 0, 0, DateTimeKind.Utc), Status = RunStatus.Success });
            runRepo.Add(new BackupRun { JobId = jobId, TargetId = targetId, RunGroupId = Guid.NewGuid(), StartedAt = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc), Status = RunStatus.Success });

            var latest = runRepo.GetLatestByJobId(jobId);

            Assert.NotNull(latest);
            Assert.Equal(new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc), latest!.StartedAt);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetByRunGroupId_ReturnsAllTargetsOfThatRun()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob
            {
                Name = "A",
                SourcePath = "C:\\a",
                Targets = new List<BackupTarget>
                {
                    new() { Type = TargetType.Local, DestinationPath = "D:\\a" },
                    new() { Type = TargetType.Local, DestinationPath = "D:\\b" },
                },
            });
            var targetIds = jobRepo.GetById(jobId)!.Targets.Select(t => t.Id).ToList();

            var runRepo = new RunRepository(connectionString);
            var groupId = Guid.NewGuid();
            runRepo.Add(new BackupRun { JobId = jobId, TargetId = targetIds[0], RunGroupId = groupId, StartedAt = DateTime.UtcNow, Status = RunStatus.Success });
            runRepo.Add(new BackupRun { JobId = jobId, TargetId = targetIds[1], RunGroupId = groupId, StartedAt = DateTime.UtcNow, Status = RunStatus.Failed });
            runRepo.Add(new BackupRun { JobId = jobId, TargetId = targetIds[0], RunGroupId = Guid.NewGuid(), StartedAt = DateTime.UtcNow.AddDays(-1), Status = RunStatus.Success });

            var groupRuns = runRepo.GetByRunGroupId(groupId);

            Assert.Equal(2, groupRuns.Count);
            Assert.All(groupRuns, r => Assert.Equal(groupId, r.RunGroupId));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetLatestRunGroupId_ReturnsMostRecentGroup()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var jobRepo = new JobRepository(connectionString);
            var jobId = jobRepo.Add(new BackupJob { Name = "A", SourcePath = "C:\\a", Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = "D:\\a" } } });
            var targetId = jobRepo.GetById(jobId)!.Targets.Single().Id;

            var runRepo = new RunRepository(connectionString);
            var olderGroup = Guid.NewGuid();
            var newerGroup = Guid.NewGuid();
            runRepo.Add(new BackupRun { JobId = jobId, TargetId = targetId, RunGroupId = olderGroup, StartedAt = DateTime.UtcNow.AddHours(-2), Status = RunStatus.Success });
            runRepo.Add(new BackupRun { JobId = jobId, TargetId = targetId, RunGroupId = newerGroup, StartedAt = DateTime.UtcNow, Status = RunStatus.Success });

            Assert.Equal(newerGroup, runRepo.GetLatestRunGroupId(jobId));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetLatestRunGroupId_ReturnsNullWhenNoRuns()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var runRepo = new RunRepository(connectionString);

            Assert.Null(runRepo.GetLatestRunGroupId(999));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
