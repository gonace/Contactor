using System.Globalization;
using Contactor.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Contactor.Web.Features.Relays;

/// <summary>Who turned a relay on, for the Relay screen's status card.</summary>
public enum TurnedOnBy
{
    /// <summary>Not known, e.g. it was turned on at the board.</summary>
    Nobody,

    /// <summary>The person looking at the screen.</summary>
    You,

    /// <summary>One of the relay's schedules.</summary>
    Schedule,

    /// <summary>Outside the app, e.g. with the board's own web page.</summary>
    Outside,

    /// <summary>Someone else or an input, by name.</summary>
    Named,
}

/// <summary>
/// A relay's status in words, in the app's language: the line under its name ("On since 14:02 · Jane · off 16:02"),
/// the Relay screen's sentences and the times in them.
/// </summary>
public sealed class RelayStatusText
{
    private readonly IStringLocalizer<Texts> _texts;

    public RelayStatusText(IStringLocalizer<Texts> texts)
    {
        _texts = texts;
    }

    /// <summary>In place of every state before the board's first answer since the app started.</summary>
    public string Waiting => _texts["Relays.Status.Waiting"];

    /// <summary>Under a relay whose press the board didn't confirm, with "Try again" and "Leave it".</summary>
    public string Unconfirmed => _texts["Relays.Status.Unconfirmed"];

    /// <param name="isOn">What the board last reported.</param>
    /// <param name="onSinceLocal">When the app turned it on, or null if it was turned on another way.</param>
    /// <param name="turnedOnBy">Who turned it on, in words: "you" for the viewer, otherwise their name.</param>
    /// <param name="autoOffAtLocal">When auto-off turns it off, or null for never.</param>
    /// <param name="nowLocal">Now, in local time.</param>
    /// <param name="offSinceLocal">When it was last turned off, or null if that isn't in the log.</param>
    /// <param name="turnedOffBy">Who turned it off, in words: "you", a name, "schedule", "auto-off" or "outside the app".</param>
    /// <param name="nextScheduleOnLocal">When a schedule next turns it on (within 24 h), or null.</param>
    public string For(
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
                return _texts["Relays.Status.ScheduleOn", When(nextScheduleOnLocal.Value, nowLocal)];
            }

            if (offSinceLocal is null)
            {
                return _texts["Relays.Status.Off"];
            }

            var off = When(offSinceLocal.Value, nowLocal);
            return string.IsNullOrEmpty(turnedOffBy)
                ? _texts["Relays.Status.OffSince", off]
                : _texts["Relays.Status.OffSinceBy", off, turnedOffBy];
        }

        if (onSinceLocal is null)
        {
            return _texts["Relays.Status.On"];
        }

        var since = When(onSinceLocal.Value, nowLocal);
        var by = !string.IsNullOrEmpty(turnedOnBy);
        if (autoOffAtLocal is null)
        {
            return by ? _texts["Relays.Status.OnSinceBy", since, turnedOnBy!] : _texts["Relays.Status.OnSince", since];
        }

        var offAt = When(autoOffAtLocal.Value, nowLocal);
        return by ? _texts["Relays.Status.OnSinceByOff", since, turnedOnBy!, offAt] : _texts["Relays.Status.OnSinceOff", since, offAt];
    }

    /// <summary>
    /// The status card on the Relay screen, in whole sentences: "On since 14:02, by Jane. Turns off 16:02." or
    /// "Off right now.".
    /// </summary>
    /// <param name="by">Who turned it on.</param>
    /// <param name="name">Their name, for <see cref="TurnedOnBy.Named"/>; with none, the sentence names nobody.</param>
    public string Sentence(bool isOn, DateTime? onSinceLocal, TurnedOnBy by, string? name, DateTime? autoOffAtLocal, DateTime nowLocal)
    {
        if (!isOn)
        {
            return _texts["Relays.Sentence.Off"];
        }

        if (onSinceLocal is null)
        {
            return _texts["Relays.Sentence.On"];
        }

        var since = When(onSinceLocal.Value, nowLocal);
        string text = by switch
        {
            TurnedOnBy.You => _texts["Relays.Sentence.OnSinceByYou", since],
            TurnedOnBy.Schedule => _texts["Relays.Sentence.OnSinceBySchedule", since],
            TurnedOnBy.Outside => _texts["Relays.Sentence.OnSinceOutside", since],
            TurnedOnBy.Named when !string.IsNullOrEmpty(name) => _texts["Relays.Sentence.OnSinceBy", since, name],
            _ => _texts["Relays.Sentence.OnSince", since],
        };

        return autoOffAtLocal is { } offAt ? _texts["Relays.Sentence.TurnsOff", text, When(offAt, nowLocal)] : text;
    }

    /// <summary>
    /// "14:02" today, "yesterday 14:02", "tomorrow 06:30", otherwise "3 Nov 14:02": the time always 24 hours with ":"
    /// in every language, the date with the language's month names.
    /// </summary>
    public string When(DateTime local, DateTime nowLocal)
    {
        var culture = CultureInfo.CurrentCulture;
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        var days = (local.Date - nowLocal.Date).Days;
        return days switch
        {
            0 => time,
            -1 => _texts["Relays.When.Yesterday", time],
            1 => _texts["Relays.When.Tomorrow", time],
            _ => _texts["Relays.When.Date", local.ToString(_texts["Relays.When.DateFormat"], culture), time],
        };
    }
}
