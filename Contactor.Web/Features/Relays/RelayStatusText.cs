using System.Globalization;

namespace Contactor.Web.Features.Relays;

/// <summary>
/// The status line under a relay's name, e.g. "On since 14:02 · Jane · off 16:02".
/// </summary>
public static class RelayStatusText
{
    /// <summary>In place of every state before the board's first answer since the app started.</summary>
    public const string Waiting = "Waiting for the board";

    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <param name="isOn">What the board last reported.</param>
    /// <param name="onSinceLocal">When the app turned it on, or null if it was turned on another way.</param>
    /// <param name="turnedOnBy">Who turned it on: "you" for the viewer, otherwise their name.</param>
    /// <param name="autoOffAtLocal">When auto-off turns it off, or null for never.</param>
    /// <param name="nowLocal">Now, in local time.</param>
    /// <param name="offSinceLocal">When it was last turned off, or null if that isn't in the log.</param>
    /// <param name="turnedOffBy">Who turned it off: "you", a name, "schedule", "auto-off" or "outside the app".</param>
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
                return $"Schedule: on {When(nextScheduleOnLocal.Value, nowLocal)}";
            }

            if (offSinceLocal is null)
            {
                return "Off";
            }

            var off = $"Off since {When(offSinceLocal.Value, nowLocal)}";
            return string.IsNullOrEmpty(turnedOffBy) ? off : $"{off} · {turnedOffBy}";
        }

        if (onSinceLocal is null)
        {
            return "On";
        }

        var text = $"On since {When(onSinceLocal.Value, nowLocal)}";
        if (!string.IsNullOrEmpty(turnedOnBy))
        {
            text += $" · {turnedOnBy}";
        }

        if (autoOffAtLocal is not null)
        {
            text += $" · off {When(autoOffAtLocal.Value, nowLocal)}";
        }

        return text;
    }

    /// <summary>
    /// The status card on the Relay screen, in whole sentences: "On since 14:02, by Jane. Turns off 16:02." or
    /// "Off right now.".
    /// </summary>
    /// <param name="turnedOnBy">"you" for the viewer, otherwise their name.</param>
    public static string Sentence(bool isOn, DateTime? onSinceLocal, string? turnedOnBy, DateTime? autoOffAtLocal, DateTime nowLocal)
    {
        if (!isOn)
        {
            return "Off right now.";
        }

        if (onSinceLocal is null)
        {
            return "On right now.";
        }

        var text = $"On since {When(onSinceLocal.Value, nowLocal)}";
        text += turnedOnBy switch
        {
            null or "" => "",
            "you" => ", by you",
            "schedule" => ", by a schedule",
            _ => $", by {turnedOnBy}",
        };

        text += ".";
        if (autoOffAtLocal is not null)
        {
            text += $" Turns off {When(autoOffAtLocal.Value, nowLocal)}.";
        }

        return text;
    }

    /// <summary>"14:02" today, "yesterday 14:02", "tomorrow 06:30", otherwise "3 Nov 14:02".</summary>
    public static string When(DateTime local, DateTime nowLocal)
    {
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        var days = (local.Date - nowLocal.Date).Days;
        return days switch
        {
            0 => time,
            -1 => $"yesterday {time}",
            1 => $"tomorrow {time}",
            _ => $"{local.Day} {Months[local.Month - 1]} {time}",
        };
    }
}
