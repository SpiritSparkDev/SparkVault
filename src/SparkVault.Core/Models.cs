namespace SparkVault.Core;

public enum ScheduleType { None, Interval, DailyAt }

public sealed class BackupJob
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public List<string> ExcludePatterns { get; set; } = new();
    public ScheduleType ScheduleType { get; set; } = ScheduleType.None;
    public int? IntervalHours { get; set; }
    public TimeOnly? DailyAtTime { get; set; }
}

public enum RunStatus { Success, Failed, Cancelled }

public sealed class BackupRun
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public RunStatus Status { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed record BackupFile(string FullPath, string RelativePath, long Size);
