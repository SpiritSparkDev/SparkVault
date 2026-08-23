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

    // Most recently quarantined location wins — the realistic case is a file quarantined at
    // most once; if the same relative path was quarantined more than once over time (created,
    // deleted, recreated, deleted again), the newest move is the one still findable on the target.
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
}
