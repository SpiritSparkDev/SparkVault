using Microsoft.Data.Sqlite;

namespace SparkVault.Core;

public sealed class RunFileRepository
{
    private readonly string _connectionString;

    public RunFileRepository(string connectionString)
    {
        _connectionString = SparkVaultDatabase.DisablePooling(connectionString);
    }

    public void AddRange(int runId, IEnumerable<RunFileRecord> files)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO RunFiles (RunId, RelativePath, Size, SourceModifiedUtc)
            VALUES ($runId, $relativePath, $size, $sourceModifiedUtc);
            """;
        var runIdParam = command.Parameters.Add("$runId", SqliteType.Integer);
        var relativePathParam = command.Parameters.Add("$relativePath", SqliteType.Text);
        var sizeParam = command.Parameters.Add("$size", SqliteType.Integer);
        var sourceModifiedUtcParam = command.Parameters.Add("$sourceModifiedUtc", SqliteType.Text);

        foreach (var file in files)
        {
            runIdParam.Value = runId;
            relativePathParam.Value = file.RelativePath;
            sizeParam.Value = file.Size;
            sourceModifiedUtcParam.Value = (object?)file.SourceModifiedUtc?.ToString("O") ?? DBNull.Value;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public List<RunFileRecord> GetByRunId(int runId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT RelativePath, Size, SourceModifiedUtc FROM RunFiles WHERE RunId = $runId;";
        command.Parameters.AddWithValue("$runId", runId);

        using var reader = command.ExecuteReader();
        var results = new List<RunFileRecord>();
        while (reader.Read())
            results.Add(new RunFileRecord(
                reader.GetString(reader.GetOrdinal("RelativePath")),
                reader.GetInt64(reader.GetOrdinal("Size")),
                reader.IsDBNull(reader.GetOrdinal("SourceModifiedUtc"))
                    ? null
                    : DateTime.Parse(reader.GetString(reader.GetOrdinal("SourceModifiedUtc")), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)));

        return results;
    }
}
