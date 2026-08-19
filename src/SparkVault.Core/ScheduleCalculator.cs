namespace SparkVault.Core;

public static class ScheduleCalculator
{
    public static bool IsDue(BackupJob job, DateTime? lastRunAt, DateTime now)
    {
        switch (job.ScheduleType)
        {
            case ScheduleType.Interval:
                if (job.IntervalHours is not { } hours)
                    return false;
                return lastRunAt is null || now - lastRunAt.Value >= TimeSpan.FromHours(hours);

            case ScheduleType.DailyAt:
                if (job.DailyAtTime is not { } targetTime)
                    return false;
                var todayTarget = now.Date + targetTime.ToTimeSpan();
                if (now < todayTarget)
                    return false;
                return lastRunAt is null || lastRunAt.Value < todayTarget;

            case ScheduleType.None:
            default:
                return false;
        }
    }
}
