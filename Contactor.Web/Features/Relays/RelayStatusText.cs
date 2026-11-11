using System.Globalization;

namespace Contactor.Web.Features.Relays;

/// <summary>
/// The status line under a relay's name, in Swedish, e.g. "På sedan 14:02 · Jane · av 16:02".
/// </summary>
public static class RelayStatusText
{
    private static readonly string[] Months = ["jan", "feb", "mars", "apr", "maj", "juni", "juli", "aug", "sep", "okt", "nov", "dec"];

    /// <param name="isOn">What the board last reported.</param>
    /// <param name="onSinceLocal">When the app turned it on, or null if it was turned on another way.</param>
    /// <param name="turnedOnBy">Who turned it on: "du" for the viewer, otherwise their name.</param>
    /// <param name="autoOffAtLocal">When auto-off turns it off, or null for never.</param>
    /// <param name="nowLocal">Now, in local time.</param>
    /// <param name="offSinceLocal">When it was last turned off, or null if that isn't in the log.</param>
    /// <param name="turnedOffBy">Who turned it off: "du", a name, "schema", "auto-av" or "utanför appen".</param>
    /// <param name="nextScheduleOnLocal">When a schedule next turns it on (within 24 h), or null.</param>
    public static string For(
        bool isOn,
        DateTime? onSinceLocal,
        string? turnedOnBy,
        DateTime? autoOffAtLocal,
        DateTime nowLocal,
        DateTime? offSinceLocal = null,
        string? turnedOffBy = null,
        DateTime? nextScheduleOnLocal = null)
    {
        if (!isOn)
        {
            // What happens next is more useful than what happened last.
            if (nextScheduleOnLocal is not null)
            {
                return $"Schema: på {When(nextScheduleOnLocal.Value, nowLocal)}";
            }

            if (offSinceLocal is null)
            {
                return "Av";
            }

            var off = $"Av sedan {When(offSinceLocal.Value, nowLocal)}";
            return string.IsNullOrEmpty(turnedOffBy) ? off : $"{off} · {turnedOffBy}";
        }

        if (onSinceLocal is null)
        {
            return "På";
        }

        var text = $"På sedan {When(onSinceLocal.Value, nowLocal)}";
        if (!string.IsNullOrEmpty(turnedOnBy))
        {
            text += $" · {turnedOnBy}";
        }

        if (autoOffAtLocal is not null)
        {
            text += $" · av {When(autoOffAtLocal.Value, nowLocal)}";
        }

        return text;
    }

    /// <summary>
    /// The status card on the Relä screen, in whole sentences: "På sedan 14:02 av Jane. Stängs av 16:02." or
    /// "Av just nu.".
    /// </summary>
    /// <param name="turnedOnBy">"du" for the viewer, otherwise their name.</param>
    public static string Sentence(bool isOn, DateTime? onSinceLocal, string? turnedOnBy, DateTime? autoOffAtLocal, DateTime nowLocal)
    {
        if (!isOn)
        {
            return "Av just nu.";
        }

        if (onSinceLocal is null)
        {
            return "På just nu.";
        }

        var text = $"På sedan {When(onSinceLocal.Value, nowLocal)}";
        text += turnedOnBy switch
        {
            null or "" => "",
            "du" => " av dig",
            "schema" => " enligt schema",
            _ => $" av {turnedOnBy}",
        };

        text += ".";
        if (autoOffAtLocal is not null)
        {
            text += $" Stängs av {When(autoOffAtLocal.Value, nowLocal)}.";
        }

        return text;
    }

    /// <summary>"14:02" today, "i går 14:02", "i morgon 06:30", otherwise "3 nov 14:02".</summary>
    public static string When(DateTime local, DateTime nowLocal)
    {
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        var days = (local.Date - nowLocal.Date).Days;
        return days switch
        {
            0 => time,
            -1 => $"i går {time}",
            1 => $"i morgon {time}",
            _ => $"{local.Day} {Months[local.Month - 1]} {time}",
        };
    }
}
