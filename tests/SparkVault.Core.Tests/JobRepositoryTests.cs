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
    public void AddThenGetById_RoundTripsAllFieldsAndTargets()
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
                Targets = new List<BackupTarget>
                {
                    new() { Type = TargetType.Local, DestinationPath = @"D:\Backups\Documents" },
                    new() { Type = TargetType.Sftp, Host = "sftp.example.com", Port = 22, Username = "u", RemotePath = "/x" },
                },
            };

            var id = repo.Add(job);
            var loaded = repo.GetById(id);

            Assert.NotNull(loaded);
            Assert.Equal("Documents", loaded!.Name);
            Assert.Equal(new List<string> { "*.tmp", "cache\\*" }, loaded.ExcludePatterns);
            Assert.Equal(ScheduleType.DailyAt, loaded.ScheduleType);
            Assert.Equal(new TimeOnly(2, 0), loaded.DailyAtTime);
            Assert.Equal(2, loaded.Targets.Count);
            Assert.Contains(loaded.Targets, t => t.Type == TargetType.Local && t.DestinationPath == @"D:\Backups\Documents");
            Assert.Contains(loaded.Targets, t => t.Type == TargetType.Sftp && t.Host == "sftp.example.com");
            Assert.All(loaded.Targets, t => Assert.NotEqual(0, t.Id));
            Assert.All(loaded.Targets, t => Assert.Equal(id, t.JobId));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void AddThenGetById_RoundTripsVerifyTargetBeforeRun()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new JobRepository(connectionString);

            var id = repo.Add(new BackupJob
            {
                Name = "Verified",
                SourcePath = "C:\\a",
                VerifyTargetBeforeRun = true,
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = "C:\\a1" } },
            });

            Assert.True(repo.GetById(id)!.VerifyTargetBeforeRun);

            var job = repo.GetById(id)!;
            job.VerifyTargetBeforeRun = false;
            repo.Update(job);

            Assert.False(repo.GetById(id)!.VerifyTargetBeforeRun);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void AddThenGetById_RoundTripsWeeklyAndMonthlySchedule()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new JobRepository(connectionString);

            var weeklyId = repo.Add(new BackupJob
            {
                Name = "Weekly",
                SourcePath = "C:\\a",
                ScheduleType = ScheduleType.Weekly,
                DailyAtTime = new TimeOnly(20, 0),
                WeeklyDay = DayOfWeek.Friday,
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = "C:\\a1" } },
            });
            var monthlyId = repo.Add(new BackupJob
            {
                Name = "Monthly",
                SourcePath = "C:\\b",
                ScheduleType = ScheduleType.Monthly,
                DailyAtTime = new TimeOnly(3, 30),
                MonthlyDay = 15,
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = "C:\\b1" } },
            });

            var weekly = repo.GetById(weeklyId)!;
            Assert.Equal(ScheduleType.Weekly, weekly.ScheduleType);
            Assert.Equal(new TimeOnly(20, 0), weekly.DailyAtTime);
            Assert.Equal(DayOfWeek.Friday, weekly.WeeklyDay);
            Assert.Null(weekly.MonthlyDay);

            var monthly = repo.GetById(monthlyId)!;
            Assert.Equal(ScheduleType.Monthly, monthly.ScheduleType);
            Assert.Equal(new TimeOnly(3, 30), monthly.DailyAtTime);
            Assert.Equal(15, monthly.MonthlyDay);
            Assert.Null(monthly.WeeklyDay);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void Update_PreservesTargetIdForUnchangedTarget()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new JobRepository(connectionString);
            var id = repo.Add(new BackupJob
            {
                Name = "Old",
                SourcePath = "C:\\a",
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = "C:\\b" } },
            });

            var job = repo.GetById(id)!;
            var targetId = job.Targets.Single().Id;
            job.Name = "New";
            job.Targets.Single().DestinationPath = "C:\\c";
            repo.Update(job);

            var reloaded = repo.GetById(id)!;
            Assert.Equal("New", reloaded.Name);
            Assert.Equal(targetId, reloaded.Targets.Single().Id);
            Assert.Equal("C:\\c", reloaded.Targets.Single().DestinationPath);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void Update_RemovesTargetsNoLongerPresent()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new JobRepository(connectionString);
            var id = repo.Add(new BackupJob
            {
                Name = "Job",
                SourcePath = "C:\\a",
                Targets = new List<BackupTarget>
                {
                    new() { Type = TargetType.Local, DestinationPath = "C:\\b" },
                    new() { Type = TargetType.Local, DestinationPath = "C:\\c" },
                },
            });

            var job = repo.GetById(id)!;
            job.Targets.RemoveAt(1);
            repo.Update(job);

            Assert.Single(repo.GetById(id)!.Targets);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void Update_AddsNewTarget()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new JobRepository(connectionString);
            var id = repo.Add(new BackupJob
            {
                Name = "Job",
                SourcePath = "C:\\a",
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = "C:\\b" } },
            });

            var job = repo.GetById(id)!;
            job.Targets.Add(new BackupTarget { Type = TargetType.Ftp, Host = "ftp.example.com", RemotePath = "/x" });
            repo.Update(job);

            Assert.Equal(2, repo.GetById(id)!.Targets.Count);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void Delete_RemovesJobAndItsTargets()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new JobRepository(connectionString);
            var id = repo.Add(new BackupJob
            {
                Name = "Temp",
                SourcePath = "C:\\a",
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = "C:\\b" } },
            });

            repo.Delete(id);

            Assert.Null(repo.GetById(id));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void GetAll_ReturnsAllJobsWithTargets()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new JobRepository(connectionString);
            repo.Add(new BackupJob { Name = "A", SourcePath = "C:\\a", Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = "C:\\a1" } } });
            repo.Add(new BackupJob { Name = "B", SourcePath = "C:\\b", Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = "C:\\b1" } } });

            var all = repo.GetAll();

            Assert.Equal(2, all.Count);
            Assert.All(all, j => Assert.Single(j.Targets));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
