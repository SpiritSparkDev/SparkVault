using Microsoft.Data.Sqlite;

namespace SparkVault.Core;

public static class SparkVaultDatabase
{
    public static void EnsureCreated(string connectionString)
    {
        // ponytail: Microsoft.Data.Sqlite pools connections by connection string, so Dispose()
        // does not release the file handle. Disable pooling so callers (esp. tests deleting the
        // db file right after use) don't hit a file-lock IOException.
        using var connection = new SqliteConnection(DisablePooling(connectionString));
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Jobs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                SourcePath TEXT NOT NULL,
                DestinationPath TEXT NOT NULL,
                ExcludePatterns TEXT NOT NULL,
                ScheduleType TEXT NOT NULL,
                IntervalHours INTEGER NULL,
                DailyAtTime TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS Runs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                JobId INTEGER NOT NULL,
                StartedAt TEXT NOT NULL,
                EndedAt TEXT NULL,
                Status TEXT NOT NULL,
                FileCount INTEGER NOT NULL,
                TotalBytes INTEGER NOT NULL,
                ErrorMessage TEXT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    internal static string DisablePooling(string connectionString) =>
        new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
}
