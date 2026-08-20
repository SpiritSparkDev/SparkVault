using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SparkVault.Core;

public sealed class RunRepository
{
    private readonly string _connectionString;

    public RunRepository(string connectionString)
    {
        _connectionString = SparkVaultDatabase.DisablePooling(connectionString);
    }

    public int Add(BackupRun run)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Runs (JobId, TargetId, RunGroupId, StartedAt, EndedAt, Status, FileCount, TotalBytes, ErrorMessage)
            VALUES ($jobId, $targetId, $runGroupId, $startedAt, $endedAt, $status, $fileCount, $totalBytes, $error);
            SELECT last_insert_rowid();
            """;
        BindRunParameters(command, run);

        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public void Update(BackupRun run)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Runs
            SET JobId = $jobId, TargetId = $targetId, RunGroupId = $runGroupId, StartedAt = $startedAt, EndedAt = $endedAt, Status = $status,
                FileCount = $fileCount, TotalBytes = $totalBytes, ErrorMessage = $error
            WHERE Id = $id;
            """;
        BindRunParameters(command, run);
        command.Parameters.AddWithValue("$id", run.Id);
        command.ExecuteNonQuery();
    }

    public List<BackupRun> GetByJobId(int jobId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Runs WHERE JobId = $jobId ORDER BY StartedAt DESC;";
        command.Parameters.AddWithValue("$jobId", jobId);

        using var reader = command.ExecuteReader();
        var runs = new List<BackupRun>();
        while (reader.Read())
            runs.Add(ReadRun(reader));

        return runs;
    }

    public BackupRun? GetLatestByJobId(int jobId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Runs WHERE JobId = $jobId ORDER BY StartedAt DESC LIMIT 1;";
        command.Parameters.AddWithValue("$jobId", jobId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    private static void BindRunParameters(SqliteCommand command, BackupRun run)
    {
        command.Parameters.AddWithValue("$jobId", run.JobId);
        command.Parameters.AddWithValue("$targetId", 1);
        command.Parameters.AddWithValue("$runGroupId", "");
        command.Parameters.AddWithValue("$startedAt", run.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$endedAt", (object?)run.EndedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", run.Status.ToString());
        command.Parameters.AddWithValue("$fileCount", run.FileCount);
        command.Parameters.AddWithValue("$totalBytes", run.TotalBytes);
        command.Parameters.AddWithValue("$error", (object?)run.ErrorMessage ?? DBNull.Value);
    }

    // RoundtripKind keeps the Kind=Utc that ToString("O") wrote; plain Parse would
    // silently shift the value to local time and stamp it Kind=Local.
    private static DateTime ParseRoundtrip(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static BackupRun ReadRun(SqliteDataReader reader)
    {
        return new BackupRun
        {
            Id = reader.GetInt32(reader.GetOrdinal("Id")),
            JobId = reader.GetInt32(reader.GetOrdinal("JobId")),
            StartedAt = ParseRoundtrip(reader.GetString(reader.GetOrdinal("StartedAt"))),
            EndedAt = reader.IsDBNull(reader.GetOrdinal("EndedAt")) ? null : ParseRoundtrip(reader.GetString(reader.GetOrdinal("EndedAt"))),
            Status = Enum.Parse<RunStatus>(reader.GetString(reader.GetOrdinal("Status"))),
            FileCount = reader.GetInt32(reader.GetOrdinal("FileCount")),
            TotalBytes = reader.GetInt64(reader.GetOrdinal("TotalBytes")),
            ErrorMessage = reader.IsDBNull(reader.GetOrdinal("ErrorMessage")) ? null : reader.GetString(reader.GetOrdinal("ErrorMessage")),
        };
    }
}
