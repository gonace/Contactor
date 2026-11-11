using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Schedules;

/// <summary>One run of a schedule: on at <see cref="OnUtc"/>, off at <see cref="OffUtc"/>, for a relay or a group.</summary>
public sealed record ScheduleWindow(int ScheduleId, int? RelayId, int? GroupId, DateTime OnUtc, DateTime OffUtc)
{
    public bool Contains(DateTime utc)
    {
        return OnUtc <= utc && utc < OffUtc;
    }
}

/// <summary>
/// Turns schedules (local days and times) into runs in UTC. A run belongs to the day it starts, also over
/// midnight. Daylight saving: a local time that doesn't exist (02:30 on the spring change) moves to the first valid
/// minute after it, and one that exists twice (02:30 on the autumn change) counts once, at its first occurrence.
/// </summary>
public static class ScheduleCalendar
{
    // A clock change is at most an hour here; this bounds the search for the next valid minute.
    private const int MaxGapMinutes = 180;

    /// <summary>Every run of the enabled schedules that overlaps <paramref name="fromUtc"/>..<paramref name="toUtc"/>.</summary>
    public static IReadOnlyList<ScheduleWindow> Windows(
        IEnumerable<Schedule> schedules,
        TimeZoneInfo zone,
        DateTime fromUtc,
        DateTime toUtc)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(zone);

        // One day either side: a run that started yesterday evening can still be running, and local dates differ
        // from UTC dates.
        var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(fromUtc, zone)).AddDays(-1);
        var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(toUtc, zone)).AddDays(1);
        List<ScheduleWindow> windows = [];
        foreach (var schedule in schedules.Where(s => s.IsEnabled))
        {
            for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
            {
                if (!schedule.Days.HasFlag(DayOf(day)))
                {
                    continue;
                }

                var onUtc = ToUtc(day, schedule.OnTime, zone);
                var offDay = schedule.OffTime > schedule.OnTime ? day : day.AddDays(1);
                var offUtc = ToUtc(offDay, schedule.OffTime, zone);

                // A clock change can squeeze a short run to nothing (on 02:10, off 02:50 on the spring change).
                if (offUtc > onUtc && offUtc > fromUtc && onUtc <= toUtc)
                {
                    windows.Add(new ScheduleWindow(schedule.Id, schedule.RelayId, schedule.GroupId, onUtc, offUtc));
                }
            }
        }

        return [.. windows.OrderBy(w => w.OnUtc)];
    }

    /// <summary>A local date and time in UTC, by the daylight saving rules above.</summary>
    public static DateTime ToUtc(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        for (var i = 0; zone.IsInvalidTime(local) && i < MaxGapMinutes; i++)
        {
            local = local.AddMinutes(1);
        }

        if (zone.IsAmbiguousTime(local))
        {
            // The first occurrence has the larger offset (summer time), so the earlier UTC instant.
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    private static ScheduleDays DayOf(DateOnly day)
    {
        return day.DayOfWeek switch
        {
            DayOfWeek.Monday => ScheduleDays.Monday,
            DayOfWeek.Tuesday => ScheduleDays.Tuesday,
            DayOfWeek.Wednesday => ScheduleDays.Wednesday,
            DayOfWeek.Thursday => ScheduleDays.Thursday,
            DayOfWeek.Friday => ScheduleDays.Friday,
            DayOfWeek.Saturday => ScheduleDays.Saturday,
            _ => ScheduleDays.Sunday,
        };
    }
}
