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
                ExcludePatterns TEXT NOT NULL,
                ScheduleType TEXT NOT NULL,
                IntervalHours INTEGER NULL,
                DailyAtTime TEXT NULL,
                WeeklyDay TEXT NULL,
                MonthlyDay INTEGER NULL,
                VerifyTargetBeforeRun INTEGER NOT NULL DEFAULT 0,
                RetentionDays INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS Targets (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                JobId INTEGER NOT NULL,
                Type TEXT NOT NULL,
                DestinationPath TEXT NULL,
                Host TEXT NULL,
                Port INTEGER NULL,
                Username TEXT NULL,
                EncryptedPassword TEXT NULL,
                RemotePath TEXT NULL,
                EncryptionMode TEXT NULL,
                PrivateKeyPath TEXT NULL,
                EncryptedKeyPassphrase TEXT NULL,
                Endpoint TEXT NULL,
                AccessKey TEXT NULL,
                EncryptedSecretKey TEXT NULL,
                Region TEXT NULL,
                Bucket TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS Runs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                JobId INTEGER NOT NULL,
                TargetId INTEGER NOT NULL,
                RunGroupId TEXT NOT NULL,
                StartedAt TEXT NOT NULL,
                EndedAt TEXT NULL,
                Status TEXT NOT NULL,
                FileCount INTEGER NOT NULL,
                TotalBytes INTEGER NOT NULL,
                ErrorMessage TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS RunFiles (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId INTEGER NOT NULL,
                RelativePath TEXT NOT NULL,
                Size INTEGER NOT NULL,
                SourceModifiedUtc TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_RunFiles_RunId ON RunFiles(RunId);

            CREATE TABLE IF NOT EXISTS QuarantinedFiles (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                JobId INTEGER NOT NULL,
                TargetId INTEGER NOT NULL,
                OriginalRelativePath TEXT NOT NULL,
                QuarantinePath TEXT NOT NULL,
                QuarantinedAtRunId INTEGER NOT NULL,
                QuarantinedAtUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_QuarantinedFiles_Lookup ON QuarantinedFiles(JobId, TargetId, OriginalRelativePath);
            """;
        command.ExecuteNonQuery();

        // CREATE TABLE IF NOT EXISTS only helps for brand-new tables — an existing Jobs table
        // from before the Weekly/Monthly schedule types were added has neither column. No
        // migration framework here (matches the rest of this pre-release app), so just add
        // whatever's missing directly.
        EnsureColumn(connection, "Jobs", "WeeklyDay", "TEXT NULL");
        EnsureColumn(connection, "Jobs", "MonthlyDay", "INTEGER NULL");
        EnsureColumn(connection, "RunFiles", "SourceModifiedUtc", "TEXT NULL");
        EnsureColumn(connection, "Jobs", "VerifyTargetBeforeRun", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "Jobs", "RetentionDays", "INTEGER NULL");
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = $"PRAGMA table_info({table});";
        using (var reader = checkCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(reader.GetOrdinal("name")), column, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alterCommand.ExecuteNonQuery();
    }

    internal static string DisablePooling(string connectionString) =>
        new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
}
