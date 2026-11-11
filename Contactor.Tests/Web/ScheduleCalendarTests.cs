using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>
/// Schedule runs in Europe/Stockholm: UTC+1 in winter, UTC+2 in summer. In 2026 summer time starts on 29 March
/// (02:00 → 03:00) and ends on 25 October (03:00 → 02:00).
/// </summary>
public sealed class ScheduleCalendarTests
{
    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    [Fact]
    public void Should_RunOnTheChosenDaysOnly()
    {
        // Monday 2 to Sunday 8 November 2026.
        var windows = Windows(Schedule(ScheduleDays.Weekdays, 6, 30, 8, 0), Utc(2026, 11, 2, 0, 0), Utc(2026, 11, 8, 23, 0));

        Assert.Equal(5, windows.Count);
        Assert.Equal(Utc(2026, 11, 2, 5, 30), windows[0].OnUtc);
        Assert.Equal(Utc(2026, 11, 2, 7, 0), windows[0].OffUtc);
        Assert.Equal(DayOfWeek.Friday, windows[^1].OnUtc.DayOfWeek);
    }

    [Fact]
    public void Should_LetARunOverMidnightBelongToTheDayItStarted()
    {
        // Friday 22:00 to Saturday 01:00; Saturday itself is not chosen.
        var windows = Windows(Schedule(ScheduleDays.Friday, 22, 0, 1, 0), Utc(2026, 11, 6, 0, 0), Utc(2026, 11, 8, 23, 0));

        var window = Assert.Single(windows);
        Assert.Equal(Utc(2026, 11, 6, 21, 0), window.OnUtc);
        Assert.Equal(Utc(2026, 11, 7, 0, 0), window.OffUtc);
    }

    [Fact]
    public void Should_IncludeARunThatStartedBeforeTheRange()
    {
        var windows = Windows(Schedule(ScheduleDays.All, 22, 0, 6, 0), Utc(2026, 11, 4, 3, 0), Utc(2026, 11, 4, 3, 0));

        Assert.Contains(windows, w => w.Contains(Utc(2026, 11, 4, 3, 0)));
    }

    [Fact]
    public void Should_RunAMissingSpringTimeAtTheNextValidMinute()
    {
        // 02:30 doesn't exist on 29 March: it runs at 03:00 local, 01:00 UTC.
        Assert.Equal(Utc(2026, 3, 29, 1, 0), ScheduleCalendar.ToUtc(new DateOnly(2026, 3, 29), new TimeOnly(2, 30), Stockholm));
        Assert.Equal(Utc(2026, 3, 29, 0, 59), ScheduleCalendar.ToUtc(new DateOnly(2026, 3, 29), new TimeOnly(1, 59), Stockholm));
    }

    [Fact]
    public void Should_RunATwiceOccurringAutumnTimeOnlyTheFirstTime()
    {
        // 02:30 happens twice on 25 October: first at 00:30 UTC (summer time), then at 01:30 UTC.
        Assert.Equal(Utc(2026, 10, 25, 0, 30), ScheduleCalendar.ToUtc(new DateOnly(2026, 10, 25), new TimeOnly(2, 30), Stockholm));

        var windows = Windows(Schedule(ScheduleDays.Sunday, 2, 30, 4, 0), Utc(2026, 10, 24, 0, 0), Utc(2026, 10, 26, 0, 0));
        var window = Assert.Single(windows);
        Assert.Equal((Utc(2026, 10, 25, 0, 30), Utc(2026, 10, 25, 3, 0)), (window.OnUtc, window.OffUtc));
    }

    [Fact]
    public void Should_SkipARunTheSpringChangeSqueezesToNothing()
    {
        // 02:10–02:50 lies wholly in the missing hour.
        Assert.Empty(Windows(Schedule(ScheduleDays.Sunday, 2, 10, 2, 50), Utc(2026, 3, 28, 0, 0), Utc(2026, 3, 30, 0, 0)));
    }

    [Fact]
    public void Should_IgnorePausedSchedules()
    {
        var paused = Schedule(ScheduleDays.All, 6, 0, 7, 0);
        paused.IsEnabled = false;

        Assert.Empty(Windows(paused, Utc(2026, 11, 2, 0, 0), Utc(2026, 11, 8, 0, 0)));
    }

    [Fact]
    public void Should_ReportTheRunningEndAndTheNextStart()
    {
        Schedule[] schedules =
        [
            Schedule(ScheduleDays.All, 6, 30, 8, 0),
            Schedule(ScheduleDays.All, 7, 0, 9, 0),
        ];

        // 07:15 local: both run; the later end counts, since ON wins.
        var running = ScheduleOutlook.For(schedules, Stockholm, Utc(2026, 11, 4, 6, 15))[1];
        Assert.Equal(Utc(2026, 11, 4, 8, 0), running.RunningUntilUtc);
        Assert.Equal(Utc(2026, 11, 5, 5, 30), running.NextOnUtc);

        var waiting = ScheduleOutlook.For(schedules, Stockholm, Utc(2026, 11, 4, 12, 0))[1];
        Assert.Null(waiting.RunningUntilUtc);
        Assert.Equal(Utc(2026, 11, 5, 5, 30), waiting.NextOnUtc);
    }

    private static List<ScheduleWindow> Windows(Schedule schedule, DateTime fromUtc, DateTime toUtc)
    {
        return [.. ScheduleCalendar.Windows([schedule], Stockholm, fromUtc, toUtc)];
    }

    private static Schedule Schedule(ScheduleDays days, int onHour, int onMinute, int offHour, int offMinute)
    {
        return new Schedule
        {
            Id = 1,
            RelayId = 1,
            Days = days,
            OnTime = new TimeOnly(onHour, onMinute),
            OffTime = new TimeOnly(offHour, offMinute),
            IsEnabled = true,
        };
    }

    private static DateTime Utc(int year, int month, int day, int hour, int minute)
    {
        return new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc);
    }
}
