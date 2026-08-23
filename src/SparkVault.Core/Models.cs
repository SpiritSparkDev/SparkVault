namespace SparkVault.Core;

public enum ScheduleType { None, Interval, DailyAt, Weekdays, Weekly, Monthly }

public enum TargetType { Local, Ftp, Sftp, S3 }

public enum FtpEncryption { None, Explicit, Implicit }

public sealed class BackupTarget
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public TargetType Type { get; set; }

    // Local
    public string? DestinationPath { get; set; }

    // Ftp / Sftp shared
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? Username { get; set; }
    public string? EncryptedPassword { get; set; }
    public string? RemotePath { get; set; }

    // Ftp only
    public FtpEncryption? EncryptionMode { get; set; }

    // Sftp only
    public string? PrivateKeyPath { get; set; }
    public string? EncryptedKeyPassphrase { get; set; }

    // S3 only. Prefix reuses RemotePath (same "destination-relative subfolder" meaning).
    public string? Endpoint { get; set; }
    public string? AccessKey { get; set; }
    public string? EncryptedSecretKey { get; set; }
    public string? Region { get; set; }
    public string? Bucket { get; set; }
}

public sealed class BackupJob
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public List<string> ExcludePatterns { get; set; } = new();
    public ScheduleType ScheduleType { get; set; } = ScheduleType.None;
    public int? IntervalHours { get; set; }
    public TimeOnly? DailyAtTime { get; set; } // also the time-of-day for Weekdays/Weekly/Monthly
    public DayOfWeek? WeeklyDay { get; set; }
    public int? MonthlyDay { get; set; } // 1-31; clamped to the last real day for shorter months
    public List<BackupTarget> Targets { get; set; } = new();
}

public enum RunStatus { Success, Failed, Cancelled }

public sealed class BackupRun
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public int TargetId { get; set; }
    public Guid RunGroupId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public RunStatus Status { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed record BackupFile(string FullPath, string RelativePath, long Size);

public sealed record RunFileRecord(string RelativePath, long Size);
