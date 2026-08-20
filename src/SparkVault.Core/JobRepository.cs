using Microsoft.Data.Sqlite;

namespace SparkVault.Core;

public sealed class JobRepository
{
    private readonly string _connectionString;

    public JobRepository(string connectionString)
    {
        _connectionString = SparkVaultDatabase.DisablePooling(connectionString);
    }

    public int Add(BackupJob job)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Jobs (Name, SourcePath, ExcludePatterns, ScheduleType, IntervalHours, DailyAtTime)
            VALUES ($name, $source, $exclude, $scheduleType, $intervalHours, $dailyAtTime);
            SELECT last_insert_rowid();
            """;
        BindJobParameters(command, job);

        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public void Update(BackupJob job)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Jobs
            SET Name = $name, SourcePath = $source,
                ExcludePatterns = $exclude, ScheduleType = $scheduleType,
                IntervalHours = $intervalHours, DailyAtTime = $dailyAtTime
            WHERE Id = $id;
            """;
        BindJobParameters(command, job);
        command.Parameters.AddWithValue("$id", job.Id);
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Jobs WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public BackupJob? GetById(int id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Jobs WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadJob(reader) : null;
    }

    public List<BackupJob> GetAll()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Jobs ORDER BY Name;";

        using var reader = command.ExecuteReader();
        var jobs = new List<BackupJob>();
        while (reader.Read())
            jobs.Add(ReadJob(reader));

        return jobs;
    }

    private static void BindJobParameters(SqliteCommand command, BackupJob job)
    {
        command.Parameters.AddWithValue("$name", job.Name);
        command.Parameters.AddWithValue("$source", job.SourcePath);
        command.Parameters.AddWithValue("$exclude", string.Join('\n', job.ExcludePatterns));
        command.Parameters.AddWithValue("$scheduleType", job.ScheduleType.ToString());
        command.Parameters.AddWithValue("$intervalHours", (object?)job.IntervalHours ?? DBNull.Value);
        command.Parameters.AddWithValue("$dailyAtTime", (object?)job.DailyAtTime?.ToString("HH:mm") ?? DBNull.Value);
    }

    private static BackupJob ReadJob(SqliteDataReader reader)
    {
        var excludeRaw = reader.GetString(reader.GetOrdinal("ExcludePatterns"));
        return new BackupJob
        {
            Id = reader.GetInt32(reader.GetOrdinal("Id")),
            Name = reader.GetString(reader.GetOrdinal("Name")),
            SourcePath = reader.GetString(reader.GetOrdinal("SourcePath")),
            ExcludePatterns = excludeRaw.Length == 0
                ? new List<string>()
                : excludeRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList(),
            ScheduleType = Enum.Parse<ScheduleType>(reader.GetString(reader.GetOrdinal("ScheduleType"))),
            IntervalHours = reader.IsDBNull(reader.GetOrdinal("IntervalHours")) ? null : reader.GetInt32(reader.GetOrdinal("IntervalHours")),
            DailyAtTime = reader.IsDBNull(reader.GetOrdinal("DailyAtTime")) ? null : TimeOnly.Parse(reader.GetString(reader.GetOrdinal("DailyAtTime"))),
        };
    }
}
