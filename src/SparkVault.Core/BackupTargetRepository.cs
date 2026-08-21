using Microsoft.Data.Sqlite;

namespace SparkVault.Core;

public sealed class BackupTargetRepository
{
    private readonly string _connectionString;

    public BackupTargetRepository(string connectionString)
    {
        _connectionString = SparkVaultDatabase.DisablePooling(connectionString);
    }

    public int Add(BackupTarget target)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Targets (JobId, Type, DestinationPath, Host, Port, Username, EncryptedPassword,
                                  RemotePath, EncryptionMode, PrivateKeyPath, EncryptedKeyPassphrase,
                                  Endpoint, AccessKey, EncryptedSecretKey, Region, Bucket)
            VALUES ($jobId, $type, $destPath, $host, $port, $username, $password,
                    $remotePath, $encMode, $keyPath, $keyPassphrase,
                    $endpoint, $accessKey, $secretKey, $region, $bucket);
            SELECT last_insert_rowid();
            """;
        BindTargetParameters(command, target);

        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public void Update(BackupTarget target)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Targets
            SET JobId = $jobId, Type = $type, DestinationPath = $destPath, Host = $host, Port = $port,
                Username = $username, EncryptedPassword = $password, RemotePath = $remotePath,
                EncryptionMode = $encMode, PrivateKeyPath = $keyPath, EncryptedKeyPassphrase = $keyPassphrase,
                Endpoint = $endpoint, AccessKey = $accessKey, EncryptedSecretKey = $secretKey,
                Region = $region, Bucket = $bucket
            WHERE Id = $id;
            """;
        BindTargetParameters(command, target);
        command.Parameters.AddWithValue("$id", target.Id);
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Targets WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public List<BackupTarget> GetByJobId(int jobId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Targets WHERE JobId = $jobId ORDER BY Id;";
        command.Parameters.AddWithValue("$jobId", jobId);

        using var reader = command.ExecuteReader();
        var targets = new List<BackupTarget>();
        while (reader.Read())
            targets.Add(ReadTarget(reader));

        return targets;
    }

    private static void BindTargetParameters(SqliteCommand command, BackupTarget target)
    {
        command.Parameters.AddWithValue("$jobId", target.JobId);
        command.Parameters.AddWithValue("$type", target.Type.ToString());
        command.Parameters.AddWithValue("$destPath", (object?)target.DestinationPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$host", (object?)target.Host ?? DBNull.Value);
        command.Parameters.AddWithValue("$port", (object?)target.Port ?? DBNull.Value);
        command.Parameters.AddWithValue("$username", (object?)target.Username ?? DBNull.Value);
        command.Parameters.AddWithValue("$password", (object?)target.EncryptedPassword ?? DBNull.Value);
        command.Parameters.AddWithValue("$remotePath", (object?)target.RemotePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$encMode", target.EncryptionMode is { } mode ? mode.ToString() : (object)DBNull.Value);
        command.Parameters.AddWithValue("$keyPath", (object?)target.PrivateKeyPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$keyPassphrase", (object?)target.EncryptedKeyPassphrase ?? DBNull.Value);
        command.Parameters.AddWithValue("$endpoint", (object?)target.Endpoint ?? DBNull.Value);
        command.Parameters.AddWithValue("$accessKey", (object?)target.AccessKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$secretKey", (object?)target.EncryptedSecretKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$region", (object?)target.Region ?? DBNull.Value);
        command.Parameters.AddWithValue("$bucket", (object?)target.Bucket ?? DBNull.Value);
    }

    private static BackupTarget ReadTarget(SqliteDataReader reader)
    {
        string? GetNullableString(string column) =>
            reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetString(reader.GetOrdinal(column));

        return new BackupTarget
        {
            Id = reader.GetInt32(reader.GetOrdinal("Id")),
            JobId = reader.GetInt32(reader.GetOrdinal("JobId")),
            Type = Enum.Parse<TargetType>(reader.GetString(reader.GetOrdinal("Type"))),
            DestinationPath = GetNullableString("DestinationPath"),
            Host = GetNullableString("Host"),
            Port = reader.IsDBNull(reader.GetOrdinal("Port")) ? null : reader.GetInt32(reader.GetOrdinal("Port")),
            Username = GetNullableString("Username"),
            EncryptedPassword = GetNullableString("EncryptedPassword"),
            RemotePath = GetNullableString("RemotePath"),
            EncryptionMode = GetNullableString("EncryptionMode") is { } m ? Enum.Parse<FtpEncryption>(m) : null,
            PrivateKeyPath = GetNullableString("PrivateKeyPath"),
            EncryptedKeyPassphrase = GetNullableString("EncryptedKeyPassphrase"),
            Endpoint = GetNullableString("Endpoint"),
            AccessKey = GetNullableString("AccessKey"),
            EncryptedSecretKey = GetNullableString("EncryptedSecretKey"),
            Region = GetNullableString("Region"),
            Bucket = GetNullableString("Bucket"),
        };
    }
}
