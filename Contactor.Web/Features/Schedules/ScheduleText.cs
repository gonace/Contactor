using System.Globalization;
using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Schedules;

/// <summary>
/// The Swedish words for a schedule's days and times, e.g. "Mån–Fre" in a list and "Måndag till fredag" in a
/// sentence.
/// </summary>
public static class ScheduleText
{
    /// <summary>Monday first, as on a Swedish calendar.</summary>
    public static readonly IReadOnlyList<(ScheduleDays Day, string Letter, string Short, string Name)> Days =
    [
        (ScheduleDays.Monday, "M", "Mån", "måndag"),
        (ScheduleDays.Tuesday, "T", "Tis", "tisdag"),
        (ScheduleDays.Wednesday, "O", "Ons", "onsdag"),
        (ScheduleDays.Thursday, "T", "Tor", "torsdag"),
        (ScheduleDays.Friday, "F", "Fre", "fredag"),
        (ScheduleDays.Saturday, "L", "Lör", "lördag"),
        (ScheduleDays.Sunday, "S", "Sön", "söndag"),
    ];

    private static readonly ScheduleDays Weekend = ScheduleDays.Saturday | ScheduleDays.Sunday;

    /// <summary>For lists: "Varje dag", "Mån–Fre", "Lör–Sön", "Lördag", or "Mån, Ons, Fre".</summary>
    public static string ShortDays(ScheduleDays days)
    {
        var chosen = Chosen(days);
        return days switch
        {
            ScheduleDays.All => "Varje dag",
            _ when days == Weekend => "Lör–Sön",
            _ when chosen.Count == 1 => Capitalize(chosen[0].Name),
            _ when IsRun(chosen) => $"{chosen[0].Short}–{chosen[^1].Short}",
            _ => string.Join(", ", chosen.Select(d => d.Short)),
        };
    }

    /// <summary>For sentences: "Varje dag", "Måndag till fredag", "Lördag och söndag", or "Måndag, onsdag och fredag".</summary>
    public static string LongDays(ScheduleDays days)
    {
        var chosen = Chosen(days);
        return days switch
        {
            ScheduleDays.None => "Inga dagar valda",
            ScheduleDays.All => "Varje dag",
            _ when chosen.Count == 1 => Capitalize(chosen[0].Name),
            _ when IsRun(chosen) => Capitalize($"{chosen[0].Name} till {chosen[^1].Name}"),
            _ => Capitalize($"{string.Join(", ", chosen.SkipLast(1).Select(d => d.Name))} och {chosen[^1].Name}"),
        };
    }

    // Three or more days in a row, Monday first ("Mån–Fre", "Tis–Lör"); two days read better as a list.
    private static bool IsRun(List<(ScheduleDays Day, string Letter, string Short, string Name)> chosen)
    {
        if (chosen.Count < 3)
        {
            return false;
        }

        var first = Days.ToList().FindIndex(d => d.Day == chosen[0].Day);
        return chosen.Select((d, i) => Days[first + i].Day == d.Day).All(inRun => inRun);
    }

    /// <summary>"Måndag till fredag: reläet slås på 06:30 och stängs av 08:00." ("… dagen efter." over midnight).</summary>
    public static string Summary(ScheduleDays days, TimeOnly on, TimeOnly off, bool group = false)
    {
        if (days == ScheduleDays.None)
        {
            return "Inga dagar valda.";
        }

        var nextDay = off < on ? " dagen efter" : "";
        return $"{LongDays(days)}: {(group ? "gruppen" : "reläet")} slås på {Time(on)} och stängs av {Time(off)}{nextDay}.";
    }

    /// <summary>"06:30 – 08:00" for a list row.</summary>
    public static string Times(TimeOnly on, TimeOnly off)
    {
        return $"{Time(on)} – {Time(off)}";
    }

    /// <summary>"mån–fre 06:30–08:00", for the event log.</summary>
    public static string Inline(ScheduleDays days, TimeOnly on, TimeOnly off)
    {
        return $"{ShortDays(days).ToLower(CultureInfo.InvariantCulture)} {Time(on)}–{Time(off)}";
    }

    public static string Time(TimeOnly time)
    {
        return time.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static List<(ScheduleDays Day, string Letter, string Short, string Name)> Chosen(ScheduleDays days)
    {
        return [.. Days.Where(d => days.HasFlag(d.Day))];
    }

    private static string Capitalize(string text)
    {
        return text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..];
    }
}
