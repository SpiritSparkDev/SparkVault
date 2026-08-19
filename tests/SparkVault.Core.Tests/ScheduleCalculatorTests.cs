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
}
