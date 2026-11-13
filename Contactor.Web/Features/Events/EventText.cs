using System.Globalization;
using System.Text.Json;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Events;

/// <summary>How an event is drawn: the badge, who did it, what they did, and to what.</summary>
/// <param name="IsPerson">A person (filled badge with their initial) rather than something automatic (dashed badge).</param>
/// <param name="Badge">The letter in the badge.</param>
/// <param name="Who">Shown in bold, e.g. "Jane" or "Schedule".</param>
/// <param name="What">Follows it, e.g. "turned on".</param>
/// <param name="Target">The line below, usually the relay's name; null when there is none.</param>
/// <param name="IsAlert">
/// Shown in red: a safety shut-off, a group that couldn't start, or a board whose relays couldn't be set to normal mode.
/// </param>
/// <param name="TimeUnknown">
/// An input's change the app only noticed after a gap in contact: the event's time is when it was noticed, not when it
/// happened, so the screens add "time unknown" with the reason (<see cref="EventText.WhyTimeUnknown"/>).
/// </param>
public sealed record EventView(bool IsPerson, string Badge, string Who, string What, string? Target, bool IsAlert = false, bool TimeUnknown = false);

/// <summary>
/// Turns stored events into the text of the Events screen.
/// </summary>
public static class EventText
{
    /// <summary>Why an input's change has "time unknown" on the Events screen.</summary>
    public const string WhyTimeUnknown =
        "The app had no contact with the board when this input changed, so it only noticed the change at this time. It may have happened any time before.";

    /// <param name="entry">The event.</param>
    /// <param name="relayName">The relay's current name, or null for events without a relay.</param>
    /// <param name="personName">The display name of the person behind it, when the actor is an e-mail, or the input's name when it is an input.</param>
    /// <param name="groupName">The group's current name, for a group's events; null for others or a deleted group.</param>
    /// <param name="inputName">The input's current name, for an input's changes; null for others.</param>
    public static EventView For(EventLogEntry entry, string? relayName, string? personName, string? groupName = null, string? inputName = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Action == EventActions.InputChanged)
        {
            return InputChange(entry, inputName);
        }

        var isPerson = IsPersonActor(entry.Actor);
        var byInput = EventActors.TryInput(entry.Actor, out var inputId);
        var who = isPerson ? personName ?? entry.Actor
            : byInput ? personName ?? Input.DefaultName(inputId)
            : WhoFor(entry);
        var badge = byInput ? "I"
            : !isPerson ? "A"
            : who.Length > 0 ? who[..1].ToUpper(CultureInfo.InvariantCulture)
            : "?";
        var group = entry.GroupId is null ? null : groupName ?? Text(entry.Details, "name") ?? "the group";
        if (group is not null && GroupText(entry, relayName, group) is { } text)
        {
            return new EventView(isPerson, badge, who, text.What, text.Target, text.IsAlert);
        }

        // A group's schedule names the group where a relay's names the relay.
        var (what, target) = WhatFor(entry, relayName ?? group);

        // In red, like a safety shut-off: a board whose inputs may switch relays past the app.
        return new EventView(isPerson, badge, who, what, target, IsAlert: entry.Action == EventActions.BoardRelayModesNotReset);
    }

    // "Door: Open". An input that is on is closed. Without its name (an input no longer on the board): "Input 1: Closed".
    // One the app only noticed after a gap has its time marked unknown.
    private static EventView InputChange(EventLogEntry entry, string? inputName)
    {
        var name = inputName ?? Input.DefaultName(entry.InputId ?? 1);
        return new EventView(false, "I", $"{name}:", Input.StateText(Bool(entry.Details, "on") == true), null, TimeUnknown: InputNoticed(entry.Details));
    }

    // A group's own events: "John turned on the group" over "Fan heater · Fan → 3 s → Heat", "Run-on: Fan keeps running
    // for 5 min", the red "Safety shut-off: Heat turned off because Fan was off". A relay of a group switched on its own is an
    // ordinary on/off and returns null.
    private static (string What, string? Target, bool IsAlert)? GroupText(EventLogEntry entry, string? relayName, string groupName)
    {
        var role = Text(entry.Details, "role") ?? relayName ?? "the relay";
        return entry.Action switch
        {
            EventActions.GroupOn => ("turned on the group", Chain(entry.Details) is { } chain ? $"{groupName} · {chain}" : groupName, false),
            EventActions.GroupOff when entry.Actor == EventActors.AutoOff => ($"turned off the group after {Duration(Number(entry.Details, "afterMinutes"))}", groupName, false),
            EventActions.GroupOff => ("turned off the group", groupName, false),
            EventActions.RunOnStart => ($"{role} keeps running for {Seconds(Number(entry.Details, "seconds"))}", groupName, false),
            EventActions.RunOnEnd => ($"{role} off", groupName, false),
            EventActions.InterlockTrip => (TripText(entry.Details), groupName, true),
            EventActions.GroupStartFailed => ($"couldn't start {Text(entry.Details, "role") ?? role}", groupName, true),
            EventActions.GroupCreate => ("created the group", groupName, false),
            EventActions.GroupEdit => ("changed the group", groupName, false),
            EventActions.GroupDelete => ("deleted the group", groupName, false),
            _ => null,
        };
    }

    // "Heat turned off because Fan was off", from {"missing":"Fan","off":["Heat"]}.
    private static string TripText(string? details)
    {
        var missing = Text(details, "missing") ?? "the relay above";
        var off = Read(details, "off") is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];
        return off.Count == 0
            ? $"{missing} was started"
            : $"{string.Join(" and ", off)} turned off because {missing} was off";
    }

    // "Fan → 3 s → Heat", from {"steps":["Fan","Heat"],"delays":[3]}.
    private static string? Chain(string? details)
    {
        if (Read(details, "steps") is not { ValueKind: JsonValueKind.Array } steps)
        {
            return null;
        }

        var names = steps.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
        var delays = Read(details, "delays") is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var s) ? s : (int?)null).ToList()
            : [];
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < names.Count; i++)
        {
            if (i > 0)
            {
                text.Append(i - 1 < delays.Count && delays[i - 1] is { } delay ? $" → {Seconds(delay)} → " : " → ");
            }

            text.Append(names[i]);
        }

        return text.Length == 0 ? null : text.ToString();
    }

    /// <summary>"30 s", "1 min 30 s", "5 min", "2 h" or "90 min": the same format as a countdown.</summary>
    public static string Seconds(int? seconds)
    {
        return seconds switch
        {
            null or < 0 => "a while",
            < 60 => $"{seconds} s",
            _ when seconds % 60 == 0 => Duration(seconds / 60),
            _ => $"{seconds / 60} min {seconds % 60} s",
        };
    }

    /// <summary>A person's e-mail, as opposed to "schedule", "auto-off", "external" or "system".</summary>
    public static bool IsPersonActor(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.Contains('@', StringComparison.Ordinal);
    }

    /// <summary>The short form for the relay screen's status line, e.g. "Off since 12:15 · schedule".</summary>
    public static string ShortWho(string actor, string? personName)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor switch
        {
            EventActors.Schedule => "schedule",
            EventActors.AutoOff => "auto-off",
            EventActors.External => "outside the app",
            EventActors.System => "the system",
            _ when EventActors.TryInput(actor, out var inputId) => personName ?? Input.DefaultName(inputId),
            _ => personName ?? actor,
        };
    }

    private static string WhoFor(EventLogEntry entry)
    {
        return entry.Action switch
        {
            EventActions.ConnectionLost or EventActions.ConnectionRestored or EventActions.BoardTimersDisabled or EventActions.BoardRelayModesReset or EventActions.BoardRelayModesNotReset => "The board",
            EventActions.RunOnStart => "Run-on:",
            EventActions.RunOnEnd => "Run-on done:",
            EventActions.InterlockTrip => "Safety shut-off:",
            EventActions.GroupStartFailed => "The group",
            _ => entry.Actor switch
            {
                EventActors.Schedule => "Schedule",
                EventActors.AutoOff => "Auto-off",
                EventActors.External => "Outside the app",
                _ => "System",
            },
        };
    }

    private static (string What, string? Target) WhatFor(EventLogEntry entry, string? relayName)
    {
        return entry.Action switch
        {
            EventActions.On => ("turned on", relayName),
            EventActions.Off when entry.Actor == EventActors.AutoOff => ($"turned off after {Duration(Number(entry.Details, "afterMinutes"))}", relayName),
            EventActions.Off => ("turned off", relayName),
            EventActions.Rename => ($"renamed {Text(entry.Details, "from") ?? relayName} to", Text(entry.Details, "to") ?? relayName),
            EventActions.Activate => ("made the relay active", relayName),
            EventActions.Deactivate => ("made the relay inactive", relayName),
            EventActions.ScheduleAdd => (WithSchedule("added schedule", "added a schedule", entry.Details), relayName),
            EventActions.ScheduleEdit when Bool(entry.Details, "enabled") is false => (WithSchedule("paused schedule", "paused a schedule", entry.Details), relayName),
            EventActions.ScheduleEdit when Bool(entry.Details, "enabled") is true => (WithSchedule("resumed schedule", "resumed a schedule", entry.Details), relayName),
            EventActions.ScheduleEdit => (WithSchedule("changed schedule to", "changed a schedule", entry.Details), relayName),
            EventActions.ScheduleDelete => (WithSchedule("deleted schedule", "deleted a schedule", entry.Details), relayName),
            EventActions.ConnectionLost => ("lost contact with the app", null),
            EventActions.ConnectionRestored => ("reconnected", null),
            EventActions.BoardTimersDisabled => ("had its own timers turned off", null),
            EventActions.BoardRelayModesReset => ("had its relays set to normal mode, so its inputs don't switch them", null),
            EventActions.BoardRelayModesNotReset => ("couldn't have its relays set to normal mode, so its inputs may switch them", null),
            EventActions.BoardLoginChanged => ("changed the relay board's login", null),
            EventActions.BoardAddressChanged => Text(entry.Details, "from") is { } from
                ? ($"changed the relay board's address from {from} to {Text(entry.Details, "to") ?? "?"}", null)
                : ($"set the relay board's address to {Text(entry.Details, "to") ?? "?"}", null),
            _ => (entry.Action, relayName),
        };
    }

    // "added schedule Mon–Fri 06:30–08:00", or the plain words when the details can't be read.
    private static string WithSchedule(string words, string fallback, string? details)
    {
        var days = Number(details, "days");
        var on = Text(details, "on");
        var off = Text(details, "off");
        if (days is not (> 0 and <= (int)ScheduleDays.All)
            || !TimeOnly.TryParseExact(on, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var onTime)
            || !TimeOnly.TryParseExact(off, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var offTime))
        {
            return fallback;
        }

        return $"{words} {ScheduleText.Inline((ScheduleDays)days.Value, onTime, offTime)}";
    }

    /// <summary>"2 h" for whole hours, otherwise "90 min".</summary>
    public static string Duration(int? minutes)
    {
        return minutes switch
        {
            null or <= 0 => "a while",
            _ when minutes % 60 == 0 => $"{minutes / 60} h",
            _ => $"{minutes} min",
        };
    }

    private static string? Text(string? json, string property)
    {
        return Read(json, property) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    }

    private static int? Number(string? json, string property)
    {
        return Read(json, property) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : null;
    }

    /// <summary>Whether an "input-changed" event says the input turned on (closed); null when it can't be read.</summary>
    public static bool? InputOn(string? details)
    {
        return Bool(details, "on");
    }

    /// <summary>Whether an "input-changed" event was only noticed after a gap, so its time is when it was noticed.</summary>
    public static bool InputNoticed(string? details)
    {
        return Bool(details, "noticed") == true;
    }

    private static bool? Bool(string? json, string property)
    {
        return Read(json, property) switch
        {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            _ => null,
        };
    }

    // Details is free-form JSON written by the app; a malformed value shows as missing instead of breaking the page.
    private static JsonElement? Read(string? json, string property)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(property, out var value)
                    ? value.Clone()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
