using Microsoft.Data.Sqlite;
using Serilog;

namespace SparkVault.Core;

public sealed class JobRepository
{
    private readonly string _connectionString;
    private readonly BackupTargetRepository _targetRepository;

    public JobRepository(string connectionString)
    {
        _connectionString = SparkVaultDatabase.DisablePooling(connectionString);
        _targetRepository = new BackupTargetRepository(connectionString);
    }

    public int Add(BackupJob job)
    {
        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Jobs (Name, SourcePath, ExcludePatterns, ScheduleType, IntervalHours, DailyAtTime, WeeklyDay, MonthlyDay, VerifyTargetBeforeRun)
                VALUES ($name, $source, $exclude, $scheduleType, $intervalHours, $dailyAtTime, $weeklyDay, $monthlyDay, $verifyTargetBeforeRun);
                SELECT last_insert_rowid();
                """;
            BindJobParameters(command, job);
            job.Id = Convert.ToInt32((long)command.ExecuteScalar()!);
        }

        foreach (var target in job.Targets)
        {
            target.JobId = job.Id;
            target.Id = _targetRepository.Add(target);
        }

        Log.Information("Job angelegt: Id={JobId}, Name={JobName}, Quellpfad={SourcePath}, Ziele={TargetCount}",
            job.Id, job.Name, job.SourcePath, job.Targets.Count);

        return job.Id;
    }

    public void Update(BackupJob job)
    {
        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE Jobs
                SET Name = $name, SourcePath = $source, ExcludePatterns = $exclude,
                    ScheduleType = $scheduleType, IntervalHours = $intervalHours, DailyAtTime = $dailyAtTime,
                    WeeklyDay = $weeklyDay, MonthlyDay = $monthlyDay, VerifyTargetBeforeRun = $verifyTargetBeforeRun
                WHERE Id = $id;
                """;
            BindJobParameters(command, job);
            command.Parameters.AddWithValue("$id", job.Id);
            command.ExecuteNonQuery();
        }

        var existingIds = _targetRepository.GetByJobId(job.Id).Select(t => t.Id).ToHashSet();
        var currentIds = job.Targets.Where(t => t.Id != 0).Select(t => t.Id).ToHashSet();

        foreach (var staleId in existingIds.Except(currentIds))
            _targetRepository.Delete(staleId);

        foreach (var target in job.Targets)
        {
            target.JobId = job.Id;
            if (target.Id == 0)
                target.Id = _targetRepository.Add(target);
            else
                _targetRepository.Update(target);
        }

        Log.Information("Job aktualisiert: Id={JobId}, Name={JobName}, Quellpfad={SourcePath}, Ziele={TargetCount}",
            job.Id, job.Name, job.SourcePath, job.Targets.Count);
    }

    public void Delete(int id)
    {
        var name = GetById(id)?.Name;

        foreach (var target in _targetRepository.GetByJobId(id))
            _targetRepository.Delete(target.Id);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Jobs WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();

        Log.Information("Job gelöscht: Id={JobId}, Name={JobName}", id, name ?? "(unbekannt)");
    }

    public BackupJob? GetById(int id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Jobs WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var job = ReadJob(reader);
        job.Targets = _targetRepository.GetByJobId(job.Id);
        return job;
    }

    public List<BackupJob> GetAll()
    {
        List<BackupJob> jobs;
        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM Jobs ORDER BY Name;";

            using var reader = command.ExecuteReader();
            jobs = new List<BackupJob>();
            while (reader.Read())
                jobs.Add(ReadJob(reader));
        }

        foreach (var job in jobs)
            job.Targets = _targetRepository.GetByJobId(job.Id);

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
        command.Parameters.AddWithValue("$weeklyDay", (object?)job.WeeklyDay?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$monthlyDay", (object?)job.MonthlyDay ?? DBNull.Value);
        command.Parameters.AddWithValue("$verifyTargetBeforeRun", job.VerifyTargetBeforeRun ? 1 : 0);
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
            WeeklyDay = reader.IsDBNull(reader.GetOrdinal("WeeklyDay")) ? null : Enum.Parse<DayOfWeek>(reader.GetString(reader.GetOrdinal("WeeklyDay"))),
            MonthlyDay = reader.IsDBNull(reader.GetOrdinal("MonthlyDay")) ? null : reader.GetInt32(reader.GetOrdinal("MonthlyDay")),
            VerifyTargetBeforeRun = reader.GetInt32(reader.GetOrdinal("VerifyTargetBeforeRun")) != 0,
        };
    }
}
