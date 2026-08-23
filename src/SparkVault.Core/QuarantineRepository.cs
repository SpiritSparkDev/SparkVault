using Microsoft.Data.Sqlite;

namespace SparkVault.Core;

public sealed class QuarantineRepository
{
    private readonly string _connectionString;

    public QuarantineRepository(string connectionString)
    {
        _connectionString = SparkVaultDatabase.DisablePooling(connectionString);
    }

    public void Add(int jobId, int targetId, string originalRelativePath, string quarantinePath, int quarantinedAtRunId, DateTime quarantinedAtUtc)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO QuarantinedFiles (JobId, TargetId, OriginalRelativePath, QuarantinePath, QuarantinedAtRunId, QuarantinedAtUtc)
            VALUES ($jobId, $targetId, $originalRelativePath, $quarantinePath, $quarantinedAtRunId, $quarantinedAtUtc);
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$targetId", targetId);
        command.Parameters.AddWithValue("$originalRelativePath", originalRelativePath);
        command.Parameters.AddWithValue("$quarantinePath", quarantinePath);
        command.Parameters.AddWithValue("$quarantinedAtRunId", quarantinedAtRunId);
        command.Parameters.AddWithValue("$quarantinedAtUtc", quarantinedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    // Most recently quarantined location wins, regardless of which run is being restored. Restores
    // must use GetQuarantinePathForRun instead — across several quarantine generations of the same
    // path the newest copy holds newer content than the run being restored expects.
    public string? GetLatestQuarantinePath(int jobId, int targetId, string originalRelativePath)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT QuarantinePath FROM QuarantinedFiles
            WHERE JobId = $jobId AND TargetId = $targetId AND OriginalRelativePath = $originalRelativePath
            ORDER BY QuarantinedAtUtc DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$targetId", targetId);
        command.Parameters.AddWithValue("$originalRelativePath", originalRelativePath);

        var result = command.ExecuteScalar();
        return result as string;
    }

    // Restore-aware lookup: the copy that belongs to the run being restored is the one made by
    // the FIRST quarantine after that run — a later generation of the same relative path (deleted,
    // recreated, backed up again, deleted again) holds different content and must not be used.
    public string? GetQuarantinePathForRun(int jobId, int targetId, string originalRelativePath, int restoringRunId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT QuarantinePath FROM QuarantinedFiles
            WHERE JobId = $jobId AND TargetId = $targetId AND OriginalRelativePath = $originalRelativePath
              AND QuarantinedAtRunId > $restoringRunId
            ORDER BY QuarantinedAtRunId ASC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$targetId", targetId);
        command.Parameters.AddWithValue("$originalRelativePath", originalRelativePath);
        command.Parameters.AddWithValue("$restoringRunId", restoringRunId);

        var result = command.ExecuteScalar();
        return result as string;
    }
}
