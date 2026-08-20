using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class JobRepositoryTests
{
    private static string NewTempDbConnectionString(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"sparkvault-test-{Guid.NewGuid():N}.db");
        return $"Data Source={dbPath}";
    }

    [Fact]
    public void AddThenGetById_RoundTripsAllFields()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new JobRepository(connectionString);

            var job = new BackupJob
            {
                Name = "Documents",
                SourcePath = @"C:\Users\me\Documents",
                ExcludePatterns = new List<string> { "*.tmp", "cache\\*" },
                ScheduleType = ScheduleType.DailyAt,
                DailyAtTime = new TimeOnly(2, 0),
            };

            var id = repo.Add(job);
            var loaded = repo.GetById(id);

            Assert.NotNull(loaded);
            Assert.Equal("Documents", loaded!.Name);
            Assert.Equal(@"C:\Users\me\Documents", loaded.SourcePath);
            Assert.Equal(new List<string> { "*.tmp", "cache\\*" }, loaded.ExcludePatterns);
            Assert.Equal(ScheduleType.DailyAt, loaded.ScheduleType);
            Assert.Equal(new TimeOnly(2, 0), loaded.DailyAtTime);
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
            var repo = new JobRepository(connectionString);
            var id = repo.Add(new BackupJob { Name = "Old", SourcePath = "C:\\a" });

            var job = repo.GetById(id)!;
            job.Name = "New";
            repo.Update(job);

            Assert.Equal("New", repo.GetById(id)!.Name);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void Delete_RemovesJob()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new JobRepository(connectionString);
            var id = repo.Add(new BackupJob { Name = "Temp", SourcePath = "C:\\a" });

            repo.Delete(id);

            Assert.Null(repo.GetById(id));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetAll_ReturnsAllJobs()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new JobRepository(connectionString);
            repo.Add(new BackupJob { Name = "A", SourcePath = "C:\\a" });
            repo.Add(new BackupJob { Name = "B", SourcePath = "C:\\b" });

            Assert.Equal(2, repo.GetAll().Count);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
