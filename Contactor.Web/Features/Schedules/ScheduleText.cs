using System.Globalization;
using Contactor.Web.Persistence;
using Contactor.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Contactor.Web.Features.Schedules;

/// <summary>
/// The words for a schedule's days and times, in the app's language, e.g. "Mon–Fri" in a list and "Monday to Friday"
/// in a sentence. The days' names are the language's own; times are always "HH:mm".
/// </summary>
public sealed class ScheduleText
{
    /// <summary>The days, Monday first, as in ISO 8601.</summary>
    public static readonly IReadOnlyList<ScheduleDays> Days =
    [
        ScheduleDays.Monday,
        ScheduleDays.Tuesday,
        ScheduleDays.Wednesday,
        ScheduleDays.Thursday,
        ScheduleDays.Friday,
        ScheduleDays.Saturday,
        ScheduleDays.Sunday,
    ];

    private static readonly ScheduleDays Weekend = ScheduleDays.Saturday | ScheduleDays.Sunday;

    private readonly IStringLocalizer<Texts> _texts;

    public ScheduleText(IStringLocalizer<Texts> texts)
    {
        _texts = texts;
    }

    /// <summary>The day's shortest name, for its button: "Mo".</summary>
    public static string Letter(ScheduleDays day) => Format.ShortestDayNames[(int)DayOfWeek(day)];

    /// <summary>The day's short name, for lists: "Mon".</summary>
    public static string ShortName(ScheduleDays day) => Format.AbbreviatedDayNames[(int)DayOfWeek(day)];

    /// <summary>The day's name, for sentences: "Monday".</summary>
    public static string Name(ScheduleDays day) => Format.DayNames[(int)DayOfWeek(day)];

    /// <summary>For lists: "Every day", "Mon–Fri", "Sat–Sun", "Saturday", or "Mon, Wed, Fri".</summary>
    public string ShortDays(ScheduleDays days)
    {
        var chosen = Chosen(days);
        return days switch
        {
            ScheduleDays.All => _texts["Schedules.EveryDay"],
            _ when days == Weekend => _texts["Schedules.Run", ShortName(ScheduleDays.Saturday), ShortName(ScheduleDays.Sunday)],
            _ when chosen.Count == 1 => Name(chosen[0]),
            _ when IsRun(chosen) => _texts["Schedules.Run", ShortName(chosen[0]), ShortName(chosen[^1])],
            _ => string.Join(_texts["Schedules.Separator"], chosen.Select(ShortName)),
        };
    }

    /// <summary>For sentences: "Every day", "Monday to Friday", "Saturday and Sunday", or "Monday, Wednesday and Friday".</summary>
    public string LongDays(ScheduleDays days)
    {
        var chosen = Chosen(days);
        return days switch
        {
            ScheduleDays.None => _texts["Schedules.NoDays"],
            ScheduleDays.All => _texts["Schedules.EveryDay"],
            _ when chosen.Count == 1 => Name(chosen[0]),
            _ when IsRun(chosen) => _texts["Schedules.RunLong", Name(chosen[0]), Name(chosen[^1])],
            _ => _texts["Schedules.And", string.Join(_texts["Schedules.Separator"], chosen.SkipLast(1).Select(Name)), Name(chosen[^1])],
        };
    }

    /// <summary>"Monday to Friday: the relay turns on at 06:30 and off at 08:00." ("… the next day." over midnight).</summary>
    public string Summary(ScheduleDays days, TimeOnly on, TimeOnly off, bool group = false)
    {
        if (days == ScheduleDays.None)
        {
            return _texts["Schedules.NoDaysSentence"];
        }

        var key = (group, off < on) switch
        {
            (true, true) => "Schedules.Summary.GroupNextDay",
            (true, false) => "Schedules.Summary.Group",
            (false, true) => "Schedules.Summary.RelayNextDay",
            (false, false) => "Schedules.Summary.Relay",
        };
        return _texts[key, LongDays(days), Time(on), Time(off)];
    }

    /// <summary>"06:30 – 08:00" for a list row.</summary>
    public string Times(TimeOnly on, TimeOnly off)
    {
        return _texts["Schedules.Times", Time(on), Time(off)];
    }

    /// <summary>"Mon–Fri 06:30–08:00", for the event log.</summary>
    public string Inline(ScheduleDays days, TimeOnly on, TimeOnly off)
    {
        return _texts["Schedules.Inline", ShortDays(days), Time(on), Time(off)];
    }

    /// <summary>"06:30": always 24 hours with ":", as a time field and the database have it too.</summary>
    public static string Time(TimeOnly time)
    {
        return time.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static DateTimeFormatInfo Format => CultureInfo.CurrentCulture.DateTimeFormat;

    private static DayOfWeek DayOfWeek(ScheduleDays day) => day switch
    {
        ScheduleDays.Monday => System.DayOfWeek.Monday,
        ScheduleDays.Tuesday => System.DayOfWeek.Tuesday,
        ScheduleDays.Wednesday => System.DayOfWeek.Wednesday,
        ScheduleDays.Thursday => System.DayOfWeek.Thursday,
        ScheduleDays.Friday => System.DayOfWeek.Friday,
        ScheduleDays.Saturday => System.DayOfWeek.Saturday,
        ScheduleDays.Sunday => System.DayOfWeek.Sunday,
        _ => throw new ArgumentOutOfRangeException(nameof(day), day, "Not one day."),
    };

    // Three or more days in a row, Monday first ("Mon–Fri", "Tue–Sat"); two days read better as a list.
    private static bool IsRun(List<ScheduleDays> chosen)
    {
        if (chosen.Count < 3)
        {
            return false;
        }

        var first = Days.ToList().IndexOf(chosen[0]);
        return chosen.Select((d, i) => Days[first + i] == d).All(inRun => inRun);
    }

    private static List<ScheduleDays> Chosen(ScheduleDays days)
    {
        return [.. Days.Where(d => days.HasFlag(d))];
    }
}
