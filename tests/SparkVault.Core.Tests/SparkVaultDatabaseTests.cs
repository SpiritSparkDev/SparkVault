using Microsoft.Data.Sqlite;
using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class SparkVaultDatabaseTests
{
    private static string NewTempDbConnectionString(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"sparkvault-test-{Guid.NewGuid():N}.db");
        return $"Data Source={dbPath}";
    }

    [Fact]
    public void EnsureCreated_AddsMissingColumnsToPreExistingJobsTable()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            // Simulate a dev database created before WeeklyDay/MonthlyDay existed: a Jobs
            // table with the old schema, which CREATE TABLE IF NOT EXISTS alone would never touch.
            var unpooledConnectionString = new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
            using (var connection = new SqliteConnection(unpooledConnectionString))
            {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE Jobs (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Name TEXT NOT NULL,
                        SourcePath TEXT NOT NULL,
                        ExcludePatterns TEXT NOT NULL,
                        ScheduleType TEXT NOT NULL,
                        IntervalHours INTEGER NULL,
                        DailyAtTime TEXT NULL
                    );
                    """;
                command.ExecuteNonQuery();
            }

            SparkVaultDatabase.EnsureCreated(connectionString);
            var repo = new JobRepository(connectionString);

            var id = repo.Add(new BackupJob
            {
                Name = "Weekly",
                SourcePath = "C:\\a",
                ScheduleType = ScheduleType.Weekly,
                DailyAtTime = new TimeOnly(20, 0),
                WeeklyDay = DayOfWeek.Friday,
                Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = "C:\\a1" } },
            });

            var loaded = repo.GetById(id)!;
            Assert.Equal(DayOfWeek.Friday, loaded.WeeklyDay);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void EnsureCreated_IsIdempotentOnAlreadyMigratedDatabase()
    {
        var connectionString = NewTempDbConnectionString(out var dbPath);
        try
        {
            SparkVaultDatabase.EnsureCreated(connectionString);
            SparkVaultDatabase.EnsureCreated(connectionString);

            var repo = new JobRepository(connectionString);
            var id = repo.Add(new BackupJob { Name = "A", SourcePath = "C:\\a", Targets = new List<BackupTarget> { new() { Type = TargetType.Local, DestinationPath = "C:\\a1" } } });
            Assert.NotNull(repo.GetById(id));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
