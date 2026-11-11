using System.Globalization;
using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Schedules;

/// <summary>
/// The words for a schedule's days and times, e.g. "Mon–Fri" in a list and "Monday to Friday" in a sentence.
/// </summary>
public static class ScheduleText
{
    /// <summary>Monday first, as in ISO 8601.</summary>
    public static readonly IReadOnlyList<(ScheduleDays Day, string Letter, string Short, string Name)> Days =
    [
        (ScheduleDays.Monday, "M", "Mon", "Monday"),
        (ScheduleDays.Tuesday, "T", "Tue", "Tuesday"),
        (ScheduleDays.Wednesday, "W", "Wed", "Wednesday"),
        (ScheduleDays.Thursday, "T", "Thu", "Thursday"),
        (ScheduleDays.Friday, "F", "Fri", "Friday"),
        (ScheduleDays.Saturday, "S", "Sat", "Saturday"),
        (ScheduleDays.Sunday, "S", "Sun", "Sunday"),
    ];

    private static readonly ScheduleDays Weekend = ScheduleDays.Saturday | ScheduleDays.Sunday;

    /// <summary>For lists: "Every day", "Mon–Fri", "Sat–Sun", "Saturday", or "Mon, Wed, Fri".</summary>
    public static string ShortDays(ScheduleDays days)
    {
        var chosen = Chosen(days);
        return days switch
        {
            ScheduleDays.All => "Every day",
            _ when days == Weekend => "Sat–Sun",
            _ when chosen.Count == 1 => chosen[0].Name,
            _ when IsRun(chosen) => $"{chosen[0].Short}–{chosen[^1].Short}",
            _ => string.Join(", ", chosen.Select(d => d.Short)),
        };
    }

    /// <summary>For sentences: "Every day", "Monday to Friday", "Saturday and Sunday", or "Monday, Wednesday and Friday".</summary>
    public static string LongDays(ScheduleDays days)
    {
        var chosen = Chosen(days);
        return days switch
        {
            ScheduleDays.None => "No days chosen",
            ScheduleDays.All => "Every day",
            _ when chosen.Count == 1 => chosen[0].Name,
            _ when IsRun(chosen) => $"{chosen[0].Name} to {chosen[^1].Name}",
            _ => $"{string.Join(", ", chosen.SkipLast(1).Select(d => d.Name))} and {chosen[^1].Name}",
        };
    }

    // Three or more days in a row, Monday first ("Mon–Fri", "Tue–Sat"); two days read better as a list.
    private static bool IsRun(List<(ScheduleDays Day, string Letter, string Short, string Name)> chosen)
    {
        if (chosen.Count < 3)
        {
            return false;
        }

        var first = Days.ToList().FindIndex(d => d.Day == chosen[0].Day);
        return chosen.Select((d, i) => Days[first + i].Day == d.Day).All(inRun => inRun);
    }

    /// <summary>"Monday to Friday: the relay turns on at 06:30 and off at 08:00." ("… the next day." over midnight).</summary>
    public static string Summary(ScheduleDays days, TimeOnly on, TimeOnly off, bool group = false)
    {
        if (days == ScheduleDays.None)
        {
            return "No days chosen.";
        }

        var nextDay = off < on ? " the next day" : "";
        return $"{LongDays(days)}: the {(group ? "group" : "relay")} turns on at {Time(on)} and off at {Time(off)}{nextDay}.";
    }

    /// <summary>"06:30 – 08:00" for a list row.</summary>
    public static string Times(TimeOnly on, TimeOnly off)
    {
        return $"{Time(on)} – {Time(off)}";
    }

    /// <summary>"Mon–Fri 06:30–08:00", for the event log.</summary>
    public static string Inline(ScheduleDays days, TimeOnly on, TimeOnly off)
    {
        return $"{ShortDays(days)} {Time(on)}–{Time(off)}";
    }

    public static string Time(TimeOnly time)
    {
        return time.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static List<(ScheduleDays Day, string Letter, string Short, string Name)> Chosen(ScheduleDays days)
    {
        return [.. Days.Where(d => days.HasFlag(d.Day))];
    }
}
