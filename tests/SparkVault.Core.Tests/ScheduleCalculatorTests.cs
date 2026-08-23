using SparkVault.Core;
using Xunit;

namespace SparkVault.Core.Tests;

public class ScheduleCalculatorTests
{
    [Fact]
    public void None_IsNeverDue()
    {
        var job = new BackupJob { ScheduleType = ScheduleType.None };
        Assert.False(ScheduleCalculator.IsDue(job, null, DateTime.UtcNow));
    }

    [Fact]
    public void Interval_DueWhenNeverRun()
    {
        var job = new BackupJob { ScheduleType = ScheduleType.Interval, IntervalHours = 6 };
        Assert.True(ScheduleCalculator.IsDue(job, null, DateTime.UtcNow));
    }

    [Fact]
    public void Interval_NotDueBeforeElapsed()
    {
        var now = new DateTime(2026, 8, 19, 12, 0, 0);
        var job = new BackupJob { ScheduleType = ScheduleType.Interval, IntervalHours = 6 };
        Assert.False(ScheduleCalculator.IsDue(job, now.AddHours(-3), now));
    }

    [Fact]
    public void Interval_DueAfterElapsed()
    {
        var now = new DateTime(2026, 8, 19, 12, 0, 0);
        var job = new BackupJob { ScheduleType = ScheduleType.Interval, IntervalHours = 6 };
        Assert.True(ScheduleCalculator.IsDue(job, now.AddHours(-7), now));
    }

    [Fact]
    public void DailyAt_NotDueBeforeTargetTime()
    {
        var now = new DateTime(2026, 8, 19, 1, 0, 0);
        var job = new BackupJob { ScheduleType = ScheduleType.DailyAt, DailyAtTime = new TimeOnly(2, 0) };
        Assert.False(ScheduleCalculator.IsDue(job, null, now));
    }

    [Fact]
    public void DailyAt_DueAfterTargetTimeIfNotRunToday()
    {
        var now = new DateTime(2026, 8, 19, 3, 0, 0);
        var job = new BackupJob { ScheduleType = ScheduleType.DailyAt, DailyAtTime = new TimeOnly(2, 0) };
        Assert.True(ScheduleCalculator.IsDue(job, now.AddDays(-1), now));
    }

    [Fact]
    public void DailyAt_NotDueIfAlreadyRunAfterTargetTimeToday()
    {
        var now = new DateTime(2026, 8, 19, 3, 0, 0);
        var job = new BackupJob { ScheduleType = ScheduleType.DailyAt, DailyAtTime = new TimeOnly(2, 0) };
        var lastRun = new DateTime(2026, 8, 19, 2, 30, 0);
        Assert.False(ScheduleCalculator.IsDue(job, lastRun, now));
    }

    // 2026-08-19 is a Wednesday; 2026-08-22 is a Saturday.
    [Fact]
    public void Weekdays_NotDueOnWeekend()
    {
        var saturday = new DateTime(2026, 8, 22, 3, 0, 0);
        var job = new BackupJob { ScheduleType = ScheduleType.Weekdays, DailyAtTime = new TimeOnly(2, 0) };
        Assert.False(ScheduleCalculator.IsDue(job, null, saturday));
    }

    [Fact]
    public void Weekdays_DueOnWeekdayAfterTargetTime()
    {
        var wednesday = new DateTime(2026, 8, 19, 3, 0, 0);
        var job = new BackupJob { ScheduleType = ScheduleType.Weekdays, DailyAtTime = new TimeOnly(2, 0) };
        Assert.True(ScheduleCalculator.IsDue(job, wednesday.AddDays(-1), wednesday));
    }

    [Fact]
    public void Weekly_NotDueOnWrongDayOfWeek()
    {
        var wednesday = new DateTime(2026, 8, 19, 3, 0, 0);
        var job = new BackupJob { ScheduleType = ScheduleType.Weekly, DailyAtTime = new TimeOnly(2, 0), WeeklyDay = DayOfWeek.Monday };
        Assert.False(ScheduleCalculator.IsDue(job, null, wednesday));
    }

    [Fact]
    public void Weekly_DueOnMatchingDayAfterTargetTimeIfNotRunThisWeek()
    {
        var monday = new DateTime(2026, 8, 24, 3, 0, 0);
        var job = new BackupJob { ScheduleType = ScheduleType.Weekly, DailyAtTime = new TimeOnly(2, 0), WeeklyDay = DayOfWeek.Monday };
        Assert.True(ScheduleCalculator.IsDue(job, monday.AddDays(-7), monday));
    }

    [Fact]
    public void Weekly_NotDueIfAlreadyRunAfterTargetTimeToday()
    {
        var monday = new DateTime(2026, 8, 24, 3, 0, 0);
        var job = new BackupJob { ScheduleType = ScheduleType.Weekly, DailyAtTime = new TimeOnly(2, 0), WeeklyDay = DayOfWeek.Monday };
        var lastRun = new DateTime(2026, 8, 24, 2, 30, 0);
        Assert.False(ScheduleCalculator.IsDue(job, lastRun, monday));
    }

    [Fact]
    public void Monthly_NotDueOnWrongDayOfMonth()
    {
        var now = new DateTime(2026, 8, 19, 3, 0, 0);
        var job = new BackupJob { ScheduleType = ScheduleType.Monthly, DailyAtTime = new TimeOnly(2, 0), MonthlyDay = 1 };
        Assert.False(ScheduleCalculator.IsDue(job, null, now));
    }

    [Fact]
    public void Monthly_DueOnMatchingDayAfterTargetTimeIfNotRunThisMonth()
    {
        var firstOfMonth = new DateTime(2026, 8, 1, 3, 0, 0);
        var job = new BackupJob { ScheduleType = ScheduleType.Monthly, DailyAtTime = new TimeOnly(2, 0), MonthlyDay = 1 };
        Assert.True(ScheduleCalculator.IsDue(job, firstOfMonth.AddMonths(-1), firstOfMonth));
    }

    [Fact]
    public void Monthly_ClampsToLastDayInShorterMonths()
    {
        // February 2026 has 28 days; a job scheduled for the 31st should fire on the 28th instead.
        var feb28 = new DateTime(2026, 2, 28, 3, 0, 0);
        var job = new BackupJob { ScheduleType = ScheduleType.Monthly, DailyAtTime = new TimeOnly(2, 0), MonthlyDay = 31 };
        Assert.True(ScheduleCalculator.IsDue(job, null, feb28));
    }

    [Fact]
    public void Monthly_NotDueIfAlreadyRunAfterTargetTimeToday()
    {
        var firstOfMonth = new DateTime(2026, 8, 1, 3, 0, 0);
        var job = new BackupJob { ScheduleType = ScheduleType.Monthly, DailyAtTime = new TimeOnly(2, 0), MonthlyDay = 1 };
        var lastRun = new DateTime(2026, 8, 1, 2, 30, 0);
        Assert.False(ScheduleCalculator.IsDue(job, lastRun, firstOfMonth));
    }
}
