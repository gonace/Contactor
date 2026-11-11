using System.Globalization;
using System.Text.Json;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Events;

/// <summary>How an event is drawn: the badge, who did it, what they did, and to what.</summary>
/// <param name="IsPerson">A person (filled badge with their initial) rather than something automatic (dashed badge).</param>
/// <param name="Badge">The letter in the badge.</param>
/// <param name="Who">Shown in bold, e.g. "Jane" or "Schema".</param>
/// <param name="What">Follows it, e.g. "slog på".</param>
/// <param name="Target">The line below, usually the relay's name; null when there is none.</param>
/// <param name="IsAlert">Shown in red: a safety shut-off, or a group that couldn't start.</param>
public sealed record EventView(bool IsPerson, string Badge, string Who, string What, string? Target, bool IsAlert = false);

/// <summary>
/// Turns stored events into the Swedish text of the Händelser screen.
/// </summary>
public static class EventText
{
    /// <param name="entry">The event.</param>
    /// <param name="relayName">The relay's current name, or null for events without a relay.</param>
    /// <param name="personName">The display name of the person behind it, when the actor is an e-mail.</param>
    /// <param name="groupName">The group's current name, for a group's events; null for others or a deleted group.</param>
    public static EventView For(EventLogEntry entry, string? relayName, string? personName, string? groupName = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var isPerson = IsPersonActor(entry.Actor);
        var who = isPerson ? personName ?? entry.Actor : WhoFor(entry);
        var badge = !isPerson ? entry.Action == EventActions.InputChanged ? "I" : "A"
            : who.Length > 0 ? who[..1].ToUpper(CultureInfo.InvariantCulture)
            : "?";
        var group = entry.GroupId is null ? null : groupName ?? Text(entry.Details, "name") ?? "gruppen";
        if (group is not null && GroupText(entry, relayName, group) is { } text)
        {
            return new EventView(isPerson, badge, who, text.What, text.Target, text.IsAlert);
        }

        // A group's schedule names the group where a relay's names the relay.
        var (what, target) = WhatFor(entry, relayName ?? group);
        return new EventView(isPerson, badge, who, what, target);
    }

    // A group's own events: "John slog på gruppen" over "Värmefläkt · Fläkt → 3 s → Värme", "Eftergång: Fläkt går 5 min
    // till", the red "Säkerhetsavstängning: Värme stängdes av eftersom Fläkt var av". A relay of a group switched on its
    // own is an ordinary on/off and returns null.
    private static (string What, string? Target, bool IsAlert)? GroupText(EventLogEntry entry, string? relayName, string groupName)
    {
        var role = Text(entry.Details, "role") ?? relayName ?? "reläet";
        return entry.Action switch
        {
            EventActions.GroupOn => ("slog på gruppen", Chain(entry.Details) is { } chain ? $"{groupName} · {chain}" : groupName, false),
            EventActions.GroupOff when entry.Actor == EventActors.AutoOff => ($"stängde av gruppen efter {Duration(Number(entry.Details, "afterMinutes"))}", groupName, false),
            EventActions.GroupOff => ("stängde av gruppen", groupName, false),
            EventActions.RunOnStart => ($"{role} går {Seconds(Number(entry.Details, "seconds"))} till", groupName, false),
            EventActions.RunOnEnd => ($"{role} avstängd", groupName, false),
            EventActions.InterlockTrip => (TripText(entry.Details), groupName, true),
            EventActions.GroupStartFailed => ($"kunde inte starta {Text(entry.Details, "role") ?? role}", groupName, true),
            EventActions.GroupCreate => ("skapade gruppen", groupName, false),
            EventActions.GroupEdit => ("ändrade gruppen", groupName, false),
            EventActions.GroupDelete => ("tog bort gruppen", groupName, false),
            _ => null,
        };
    }

    // "Värme stängdes av eftersom Fläkt var av", from {"missing":"Fläkt","off":["Värme"]}.
    private static string TripText(string? details)
    {
        var missing = Text(details, "missing") ?? "reläet ovanför";
        var off = Read(details, "off") is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];
        return off.Count == 0
            ? $"{missing} startades"
            : $"{string.Join(" och ", off)} stängdes av eftersom {missing} var av";
    }

    // "Fläkt → 3 s → Värme", from {"steps":["Fläkt","Värme"],"delays":[3]}.
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

    /// <summary>"30 s", "5 min", "2 h" or "90 min".</summary>
    public static string Seconds(int? seconds)
    {
        return seconds switch
        {
            null or < 0 => "en stund",
            < 60 => $"{seconds} s",
            _ when seconds % 60 == 0 => Duration(seconds / 60),
            _ => $"{((long)seconds + 59) / 60} min",
        };
    }

    /// <summary>A person's e-mail, as opposed to "schedule", "auto-off", "external" or "system".</summary>
    public static bool IsPersonActor(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.Contains('@', StringComparison.Ordinal);
    }

    /// <summary>The short form for the relay screen's status line, e.g. "Av sedan 12:15 · schema".</summary>
    public static string ShortWho(string actor, string? personName)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor switch
        {
            EventActors.Schedule => "schema",
            EventActors.AutoOff => "auto-av",
            EventActors.External => "utanför appen",
            EventActors.System => "systemet",
            _ => personName ?? actor,
        };
    }

    private static string WhoFor(EventLogEntry entry)
    {
        return entry.Action switch
        {
            EventActions.ConnectionLost or EventActions.ConnectionRestored or EventActions.BoardTimersDisabled => "Kortet",
            EventActions.InputChanged => "Ingången",
            EventActions.RunOnStart => "Eftergång:",
            EventActions.RunOnEnd => "Eftergång klar:",
            EventActions.InterlockTrip => "Säkerhetsavstängning:",
            EventActions.GroupStartFailed => "Gruppen",
            _ => entry.Actor switch
            {
                EventActors.Schedule => "Schema",
                EventActors.AutoOff => "Auto-av",
                EventActors.External => "Utanför appen",
                _ => "Systemet",
            },
        };
    }

    private static (string What, string? Target) WhatFor(EventLogEntry entry, string? relayName)
    {
        var external = entry.Actor == EventActors.External;
        return entry.Action switch
        {
            EventActions.On => (external ? "slogs på" : "slog på", relayName),
            EventActions.Off when entry.Actor == EventActors.AutoOff => ($"stängde av efter {Duration(Number(entry.Details, "afterMinutes"))}", relayName),
            EventActions.Off => (external ? "stängdes av" : "stängde av", relayName),
            EventActions.Rename => ($"döpte om {Text(entry.Details, "from") ?? relayName} till", Text(entry.Details, "to") ?? relayName),
            EventActions.Activate => ("gjorde reläet aktivt", relayName),
            EventActions.Deactivate => ("gjorde reläet inaktivt", relayName),
            EventActions.ScheduleAdd => (WithSchedule("lade till schema", "lade till ett schema", entry.Details), relayName),
            EventActions.ScheduleEdit when Bool(entry.Details, "enabled") is false => (WithSchedule("pausade schema", "pausade ett schema", entry.Details), relayName),
            EventActions.ScheduleEdit when Bool(entry.Details, "enabled") is true => (WithSchedule("aktiverade schema", "aktiverade ett schema", entry.Details), relayName),
            EventActions.ScheduleEdit => (WithSchedule("ändrade schema till", "ändrade ett schema", entry.Details), relayName),
            EventActions.ScheduleDelete => (WithSchedule("tog bort schema", "tog bort ett schema", entry.Details), relayName),
            EventActions.ConnectionLost => ("tappade kontakten med appen", null),
            EventActions.ConnectionRestored => ("fick kontakt igen", null),
            EventActions.InputChanged => (Bool(entry.Details, "on") == true ? "slogs på" : "slogs av", null),
            EventActions.BoardTimersDisabled => ("fick sina egna timers avstängda", null),
            EventActions.BoardLoginChanged => ("ändrade reläkortets inloggning", null),
            EventActions.BoardAddressChanged => ($"ändrade reläkortets adress från {Text(entry.Details, "from") ?? "?"} till {Text(entry.Details, "to") ?? "?"}", null),
            _ => (entry.Action, relayName),
        };
    }

    // "lade till schema mån–fre 06:30–08:00", or the plain words when the details can't be read.
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
            null or <= 0 => "en stund",
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
