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
                if (job.DailyAtTime is not { } dailyTime)
                    return false;
                return IsDueAtOrAfter(lastRunAt, now, now.Date + dailyTime.ToTimeSpan());

            case ScheduleType.Weekdays:
                if (job.DailyAtTime is not { } weekdayTime)
                    return false;
                if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                    return false;
                return IsDueAtOrAfter(lastRunAt, now, now.Date + weekdayTime.ToTimeSpan());

            case ScheduleType.Weekly:
                if (job.DailyAtTime is not { } weeklyTime || job.WeeklyDay is not { } weeklyDay)
                    return false;
                if (now.DayOfWeek != weeklyDay)
                    return false;
                return IsDueAtOrAfter(lastRunAt, now, now.Date + weeklyTime.ToTimeSpan());

            case ScheduleType.Monthly:
                if (job.DailyAtTime is not { } monthlyTime || job.MonthlyDay is not { } monthlyDay)
                    return false;
                var effectiveDay = Math.Min(monthlyDay, DateTime.DaysInMonth(now.Year, now.Month));
                if (now.Day != effectiveDay)
                    return false;
                return IsDueAtOrAfter(lastRunAt, now, now.Date + monthlyTime.ToTimeSpan());

            case ScheduleType.OnChange:
                return false;

            case ScheduleType.None:
            default:
                return false;
        }
    }

    private static bool IsDueAtOrAfter(DateTime? lastRunAt, DateTime now, DateTime target)
    {
        if (now < target)
            return false;
        return lastRunAt is null || lastRunAt.Value < target;
    }
}
