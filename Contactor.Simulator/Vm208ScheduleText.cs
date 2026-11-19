using System.Globalization;
using Contactor.Boards.Velleman;

namespace Contactor.Simulator;

/// <summary>A VM208 relay's week schedule in plain words for the page, e.g. "Mon–Fri on 06:30, off 22:00 · Sat on 08:00".</summary>
public static class Vm208ScheduleText
{
    // Monday first, as people read a week; the firmware counts Sunday as 0.
    private static readonly int[] Week = [1, 2, 3, 4, 5, 6, 0];

    private static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    /// <summary>
    /// The alarms that are on, by the days that share the same times: consecutive days as a range, e.g. "Mon–Fri".
    /// "No alarm on" when none is. A time the firmware stores but never runs (an hour above 23 or a minute above 59)
    /// shows as it is.
    /// </summary>
    public static string Describe(IReadOnlyList<Vm208Alarm> alarms)
    {
        ArgumentNullException.ThrowIfNull(alarms);
        List<(string Times, List<int> Days)> groups = [];
        foreach (var day in Week)
        {
            var on = alarms.FirstOrDefault(a => a.DayOfWeek == day && a.TurnsOn && a.Enabled);
            var off = alarms.FirstOrDefault(a => a.DayOfWeek == day && !a.TurnsOn && a.Enabled);
            if (on is null && off is null)
            {
                continue;
            }

            var times = string.Join(", ", new[] { on is null ? null : $"on {Time(on)}", off is null ? null : $"off {Time(off)}" }.OfType<string>());
            if (groups.FindIndex(g => g.Times == times) is var index and >= 0)
            {
                groups[index].Days.Add(day);
            }
            else
            {
                groups.Add((times, [day]));
            }
        }

        return groups.Count == 0 ? "No alarm on" : string.Join(" · ", groups.Select(g => $"{Days(g.Days)} {g.Times}"));
    }

    private static string Time(Vm208Alarm alarm)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{alarm.Hour:00}:{alarm.Minute:00}");
    }

    // "Mon–Fri", "Mon, Wed" or "Sat–Sun": days in the week's order, consecutive ones of three or more as a range.
    private static string Days(List<int> days)
    {
        var positions = days.Select(d => Array.IndexOf(Week, d)).Order().ToList();
        List<string> parts = [];
        for (var i = 0; i < positions.Count;)
        {
            var end = i;
            while (end + 1 < positions.Count && positions[end + 1] == positions[end] + 1)
            {
                end++;
            }

            var first = DayNames[Week[positions[i]]];
            var last = DayNames[Week[positions[end]]];
            parts.Add(end - i >= 2 ? $"{first}–{last}" : end > i ? $"{first}, {last}" : first);
            i = end + 1;
        }

        return string.Join(", ", parts);
    }
}
