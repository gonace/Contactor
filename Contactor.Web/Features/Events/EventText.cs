using System.Globalization;
using System.Text.Json;
using Contactor.Boards;
using Contactor.Web.Components;
using Contactor.Web.Features.Inputs;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;
using Contactor.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Contactor.Web.Features.Events;

/// <summary>How an event is drawn: the badge, who did it, what they did, and to what.</summary>
/// <param name="IsPerson">A person (filled badge with their initial) rather than something automatic (dashed badge).</param>
/// <param name="Badge">The letter in the badge.</param>
/// <param name="Who">Shown in bold, e.g. "Jane" or "Schedule".</param>
/// <param name="Line">
/// The whole line, with <c>{0}</c> where <see cref="Who"/> goes, e.g. "{0} turned on", so a language puts the name where
/// its word order needs it (<see cref="Sentence"/>). Any other brace in it is doubled.
/// </param>
/// <param name="Target">The line below, usually the relay's name; null when there is none.</param>
/// <param name="IsAlert">
/// Shown in red: a safety shut-off, a group that couldn't start, or a board whose relays couldn't be set to normal mode.
/// </param>
/// <param name="TimeUnknown">
/// An input's change the app only noticed after a gap in contact: the event's time is when it was noticed, not when it
/// happened, so the screens add "time unknown" with the reason (<see cref="EventText.WhyTimeUnknown"/>).
/// </param>
/// <param name="Log">An update's log ("Show what happened"), relative to the app's base; null for other events.</param>
public sealed record EventView(bool IsPerson, string Badge, string Who, string Line, string? Target, bool IsAlert = false, bool TimeUnknown = false, string? Log = null)
{
    /// <summary>The line as plain text, with the name in it: "Jane turned on".</summary>
    public string Text => string.Concat(Sentence.Parts(Line).Select(p => p.Words ?? (p.Arg == 0 ? Who : "")));

    /// <summary>The line without the name, as plain text: "turned on".</summary>
    public string What => string.Concat(Sentence.Parts(Line).Select(p => p.Words ?? "")).Trim();
}

/// <summary>
/// Turns stored events into the text of the Events screen, in the app's language, and the lengths and short names
/// other screens show the same way.
/// </summary>
public sealed class EventText
{
    private readonly IStringLocalizer<Texts> _texts;
    private readonly ScheduleText _schedules;
    private readonly InputsText _inputs;

    public EventText(IStringLocalizer<Texts> texts, ScheduleText schedules, InputsText inputs)
    {
        _texts = texts;
        _schedules = schedules;
        _inputs = inputs;
    }

    /// <summary>Why an input's change has "time unknown" on the Events screen.</summary>
    public string WhyTimeUnknown => _texts["Events.WhyTimeUnknown"];

    /// <param name="entry">The event.</param>
    /// <param name="relayName">
    /// The relay's current name, or null for events without a relay; null for an event's relay that is no longer in the
    /// app, which shows as "a removed relay".
    /// </param>
    /// <param name="personName">The display name of the person behind it, when the actor is an e-mail, or the input's name when it is an input.</param>
    /// <param name="groupName">
    /// The group's current name, for a group's events; null for others or a removed group, which shows the name saved
    /// in the event, otherwise "a removed group".
    /// </param>
    /// <param name="inputName">The input's current name, for an input's changes; null for others or a removed input.</param>
    /// <param name="boardName">
    /// The board's name, with two or more boards: an event about the board itself names it ("Workshop had its own
    /// timers turned off"); null for "The board".
    /// </param>
    public EventView For(EventLogEntry entry, string? relayName, string? personName, string? groupName = null, string? inputName = null, string? boardName = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Action == EventActions.InputChanged)
        {
            return InputChange(entry, inputName);
        }

        if (Update(entry) is { } update)
        {
            var person = IsPersonActor(entry.Actor);
            var name = person ? personName ?? entry.Actor : _texts["Events.Who.Contactor"];
            return update with { IsPerson = person, Badge = person && name.Length > 0 ? name[..1].ToUpper(CultureInfo.CurrentCulture) : "A", Who = name };
        }

        var isPerson = IsPersonActor(entry.Actor);
        if (entry.Action == EventActions.BoardOwnRuleFound && !isPerson)
        {
            return OwnRuleFound(entry, relayName, boardName);
        }

        var byInput = EventActors.TryInput(entry.Actor, out var inputId);
        var who = isPerson ? personName ?? entry.Actor
            : byInput ? personName ?? _texts["Events.RemovedInput"]
            : IsAboutBoard(entry) ? boardName ?? _texts["Events.Who.Board"]
            : WhoFor(entry);
        var badge = byInput ? "I"
            : !isPerson ? "A"
            : who.Length > 0 ? who[..1].ToUpper(CultureInfo.CurrentCulture)
            : "?";
        var group = entry.GroupId is null ? null : groupName ?? Text(entry.Details, "name") ?? _texts["Events.RemovedGroup"];

        // A relay id with no name is a relay no longer in the app, e.g. of a removed board: never its id.
        var relay = relayName ?? (entry.RelayId is null ? null : _texts["Events.RemovedRelay"].Value);
        if (group is not null && GroupText(entry, relay, group) is { } text)
        {
            return new EventView(isPerson, badge, who, text.Line, text.Target, text.IsAlert);
        }

        // A group's schedule names the group where a relay's names the relay.
        var (line, target) = LineFor(entry, relay ?? group, boardName);

        // In red, like a safety shut-off: a board whose inputs may switch relays past the app.
        return new EventView(isPerson, badge, who, line, target, IsAlert: entry.Action == EventActions.BoardRelayModesNotReset);
    }

    // "Door: Open". An input that is on is closed. Without its name (an input no longer in the app): "A removed input: Closed".
    // One the app only noticed after a gap has its time marked unknown.
    private EventView InputChange(EventLogEntry entry, string? inputName)
    {
        var name = inputName ?? _texts["Events.RemovedInput"];
        return new EventView(false, "I", $"{name}:", Line("Events.Input", _inputs.State(Bool(entry.Details, "on") == true)), null, TimeUnknown: InputNoticed(entry.Details));
    }

    // An update from Settings: "John started the update to 2026.11.20", "Contactor updated to 2026.11.20", the red
    // "Contactor went back to 2026.11.14: the update to 2026.11.20 didn't work", with its log. Null for other events.
    private EventView? Update(EventLogEntry entry)
    {
        var to = Text(entry.Details, "to") ?? _texts["Events.Update.NewVersion"];
        var log = Text(entry.Details, "id") is { } id && Updates.HelperStatus.IsId(id) ? Updates.UpdateEndpoints.LogLink(id) : null;
        var (line, alert) = entry.Action switch
        {
            EventActions.UpdateRequested => (Line("Events.Update.Requested", to), false),
            EventActions.UpdateDone => (Line("Events.Update.Done", to), false),
            EventActions.UpdateWentBack => (Line("Events.Update.WentBack", Text(entry.Details, "from") ?? _texts["Events.Update.VersionBefore"], to), true),
            EventActions.UpdateCurrent => (Line("Events.Update.Current", Text(entry.Details, "from") ?? _texts["Events.Update.VersionItRuns"]), false),
            EventActions.UpdateFailed when Text(entry.Details, "reason") == Updates.UpdateReasons.Cancelled => (Line("Events.Update.Cancelled", to), false),
            EventActions.UpdateFailed when Text(entry.Details, "reason") == Updates.UpdateReasons.Switching => (Line("Events.Update.Switching", to), false),
            EventActions.UpdateFailed => (Line("Events.Update.Failed", to, FailedBecause(Text(entry.Details, "reason"))), true),
            _ => ((string?)null, false),
        };
        return line is null ? null : new EventView(false, "A", "", line, null, alert, Log: entry.Action == EventActions.UpdateRequested ? null : log);
    }

    private string FailedBecause(string? reason)
    {
        return reason switch
        {
            Updates.UpdateReasons.Download => _texts["Events.Update.Because.Download"],
            Updates.UpdateReasons.Signature => _texts["Events.Update.Because.Signature"],
            Updates.UpdateReasons.Space => _texts["Events.Update.Because.Space"],
            Updates.UpdateReasons.Backup => _texts["Events.Update.Because.Backup"],
            Updates.UpdateReasons.Interrupted => _texts["Events.Update.Because.Interrupted"],
            _ => _texts["Events.Update.Because.Other"],
        };
    }

    // A rule of the board's own the app saw, with the board as the subject: "The board has its own timer for relay 3",
    // "The board's own timer for relay 3 was turned on" (new: while the app watched), "Relay 3 became unavailable: the
    // board controls it itself". With two or more boards, the board's name in place of "the board".
    private EventView OwnRuleFound(EventLogEntry entry, string? relayName, string? boardName)
    {
        var isNew = Bool(entry.Details, "new") == true;
        var relay = RelayNumber(entry.Details);
        OwnRuleKind? kind = OwnRuleKinds.TryParse(Text(entry.Details, "kind"), out var parsed) ? parsed : null;
        var theBoard = boardName ?? _texts["Events.OwnRules.Who.TheBoard"];
        var (key, who) = (kind, isNew) switch
        {
            (OwnRuleKind.InputLinkage, false) => ("Events.OwnRules.Found.Linkage", theBoard),
            (OwnRuleKind.InputLinkage, true) => ("Events.OwnRules.FoundNew.Linkage", theBoard),
            (OwnRuleKind.InputToggle, false) => ("Events.OwnRules.Found.Toggle", theBoard),
            (OwnRuleKind.InputToggle, true) => ("Events.OwnRules.FoundNew.Toggle", theBoard),
            (OwnRuleKind.InputEdge, false) => ("Events.OwnRules.Found.Edge", theBoard),
            (OwnRuleKind.InputEdge, true) => ("Events.OwnRules.FoundNew.Edge", theBoard),
            (OwnRuleKind.Timer, false) => ("Events.OwnRules.Found.Timer", boardName ?? _texts["Events.Who.Board"]),
            (OwnRuleKind.Timer, true) => ("Events.OwnRules.FoundNew.Timer", BoardsOwn(boardName)),
            (_, false) => ("Events.OwnRules.Found.Rule", boardName ?? _texts["Events.Who.Board"]),
            (_, true) => ("Events.OwnRules.FoundNew.Rule", BoardsOwn(boardName)),
        };
        var target = relayName ?? (entry.RelayId is null ? null : _texts["Events.RemovedRelay"].Value);
        return new EventView(false, "A", who, Line(key, relay), target);
    }

    // "The board's" or "Garage's", as the subject of a line.
    private string BoardsOwn(string? boardName)
    {
        return boardName is null ? _texts["Events.OwnRules.Who.TheBoards"] : Genitive(boardName);
    }

    // A board's name in the genitive: "Garage's", "Garages". Swedish adds no s to a name that ends in s, x or z
    // ("Lars egna timer"), so such a name has a text of its own.
    private string Genitive(string boardName)
    {
        return boardName.Length > 0 && char.ToLowerInvariant(boardName[^1]) is 's' or 'x' or 'z'
            ? _texts["Events.OwnRules.NamedEndingInS", boardName]
            : _texts["Events.OwnRules.Named", boardName];
    }

    // A person turned off or kept rules of the board's own: "turned off the board's own timers for relays 3 and 5",
    // "kept Garage's own timer for relay 3", "reset relay 3" (a relay the board controlled by itself).
    private string OwnRulesLine(string key, EventLogEntry entry, string? boardName)
    {
        var board = boardName is null ? _texts["Events.OwnRules.TheBoards"].Value : Genitive(boardName);
        List<int> relays;
        List<OwnRuleKind?> kinds;
        if (entry.Action == EventActions.BoardOwnRuleKept)
        {
            relays = Number(entry.Details, "relay") is { } relay ? [relay] : [];
            kinds = [OwnRuleKinds.TryParse(Text(entry.Details, "kind"), out var kind) ? kind : null];
        }
        else
        {
            relays = Read(entry.Details, "relays") is { ValueKind: JsonValueKind.Array } list
                ? [.. list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out _)).Select(e => e.GetInt32())]
                : [];
            kinds = Read(entry.Details, "kinds") is { ValueKind: JsonValueKind.Array } texts
                ? [.. texts.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String && OwnRuleKinds.TryParse(e.GetString(), out var k) ? k : (OwnRuleKind?)null)]
                : [];
        }

        var several = relays.Count > 1;
        var distinct = kinds.Distinct().ToList();
        var numbers = relays.Count == 0
            ? _texts["Events.Unknown"].Value
            : relays.Count == 1
                ? relays[0].ToString(CultureInfo.CurrentCulture)
                : string.Join(", ", relays.SkipLast(1).Select(r => r.ToString(CultureInfo.CurrentCulture))) + _texts["Events.List.And"] + relays[^1].ToString(CultureInfo.CurrentCulture);
        var which = _texts[several ? "Events.OwnRules.Relays" : "Events.OwnRules.Relay", numbers].Value;

        // Relays the board controlled by itself, set back to normal: "Erik reset relay 3".
        if (entry.Action == EventActions.BoardOwnRulesTurnedOff && distinct is [OwnRuleKind.InputLinkage])
        {
            return Line("Events.OwnRules.Reset", which);
        }

        var what = distinct switch
        {
            [OwnRuleKind.Timer] => several ? "Events.OwnRules.Kind.Timers" : "Events.OwnRules.Kind.Timer",
            _ when distinct.Count > 0 && distinct.All(k => k is OwnRuleKind.InputLinkage or OwnRuleKind.InputToggle or OwnRuleKind.InputEdge)
                => several ? "Events.OwnRules.Kind.InputRules" : "Events.OwnRules.Kind.InputRule",
            _ => several ? "Events.OwnRules.Kind.Rules" : "Events.OwnRules.Kind.Rule",
        };
        return Line(key, board, _texts[what], which);
    }

    // The relay's number on the board in an own rule's event, "?" when it can't be read.
    private string RelayNumber(string? details)
    {
        return Number(details, "relay") is { } relay ? relay.ToString(CultureInfo.CurrentCulture) : _texts["Events.Unknown"];
    }

    // A group's own events: "John turned on the group" over "Fan heater · Fan → 3 s → Heat", "Run-on for 5 min: Fan",
    // the red "Safety shut-off: Heat turned off. Fan must be on first." A step's name may be plural ("Fans"), so no verb
    // agrees with it. A relay of a group switched on its own is an ordinary on/off and returns null.
    private (string Line, string? Target, bool IsAlert)? GroupText(EventLogEntry entry, string? relayName, string groupName)
    {
        var role = Text(entry.Details, "role") ?? relayName ?? _texts["Events.TheRelay"];
        return entry.Action switch
        {
            EventActions.GroupOn => (Line("Events.Group.On"), Chain(entry.Details) is { } chain ? _texts["Events.Group.Target", groupName, chain] : groupName, false),
            EventActions.GroupOff when entry.Actor == EventActors.AutoOff => (Line("Events.Group.OffAfter", Duration(Number(entry.Details, "afterMinutes"))), groupName, false),
            EventActions.GroupOff => (Line("Events.Group.Off"), groupName, false),
            EventActions.RunOnStart => (Line("Events.Group.RunOnStart", role, Seconds(Number(entry.Details, "seconds"))), groupName, false),
            EventActions.RunOnEnd => (Line("Events.Group.RunOnEnd", role), groupName, false),
            EventActions.InterlockTrip => (TripLine(entry.Details), groupName, true),
            EventActions.GroupStartFailed => (Line("Events.Group.StartFailed", Text(entry.Details, "role") ?? role), groupName, true),
            EventActions.GroupCreate => (Line("Events.Group.Create"), groupName, false),
            EventActions.GroupEdit => (Line("Events.Group.Edit"), groupName, false),
            EventActions.GroupDelete => (Line("Events.Group.Delete"), groupName, false),
            _ => null,
        };
    }

    // "Heat turned off. Fan must be on first.", from {"missing":"Fan","off":["Heat"]}; with nothing turned off, the
    // step above was started so it can run on: "Fan turned on to run on".
    private string TripLine(string? details)
    {
        var missing = Text(details, "missing") ?? _texts["Events.TheRelayAbove"];
        var off = Read(details, "off") is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];
        return off.Count == 0
            ? Line("Events.Group.TripStarted", missing)
            : Line("Events.Group.Trip", string.Join(_texts["Events.List.And"], off), missing);
    }

    // "Fan → 3 s → Heat", from {"steps":["Fan","Heat"],"delays":[3]}.
    private string? Chain(string? details)
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
    public string Seconds(int? seconds)
    {
        return seconds switch
        {
            null or < 0 => _texts["Events.Length.AWhile"],
            < 60 => _texts["Events.Length.Seconds", seconds],
            _ when seconds % 60 == 0 => Duration(seconds / 60),
            _ => _texts["Events.Length.MinutesSeconds", seconds / 60, seconds % 60],
        };
    }

    /// <summary>"2 h" for whole hours, otherwise "90 min".</summary>
    public string Duration(int? minutes)
    {
        return minutes switch
        {
            null or <= 0 => _texts["Events.Length.AWhile"],
            _ when minutes % 60 == 0 => _texts["Events.Length.Hours", minutes / 60],
            _ => _texts["Events.Length.Minutes", minutes],
        };
    }

    /// <summary>A person's e-mail, as opposed to "schedule", "auto-off", "external" or "system".</summary>
    public static bool IsPersonActor(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.Contains('@', StringComparison.Ordinal);
    }

    /// <summary>The short form for the relay screen's status line, e.g. "Off since 12:15 · schedule".</summary>
    public string ShortWho(string actor, string? personName)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor switch
        {
            EventActors.Schedule => _texts["Events.Short.Schedule"],
            EventActors.AutoOff => _texts["Events.Short.AutoOff"],
            EventActors.External => _texts["Events.Short.Outside"],
            EventActors.System => _texts["Events.Short.System"],
            _ when EventActors.TryInput(actor, out _) => personName ?? _texts["Events.Short.RemovedInput"],
            _ => personName ?? actor,
        };
    }

    /// <summary>An event the board itself is the subject of, e.g. "The board lost contact with the app".</summary>
    public static bool IsAboutBoard(EventLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return !IsPersonActor(entry.Actor) && !EventActors.TryInput(entry.Actor, out _) && entry.Action
            is EventActions.ConnectionLost
            or EventActions.ConnectionRestored
            or EventActions.BoardTimersDisabled
            or EventActions.BoardRelayModesReset
            or EventActions.BoardRelayModesNotReset
            or EventActions.BoardOwnRuleFound;
    }

    /// <summary>
    /// An event whose line names its board (<see cref="IsAboutBoard"/>, or a person's turning off or keeping the board's
    /// own rules), so the Events screen doesn't name it again before the target.
    /// </summary>
    public static bool NamesBoard(EventLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return IsAboutBoard(entry) || entry.Action is EventActions.BoardOwnRulesTurnedOff or EventActions.BoardOwnRuleKept;
    }

    private string WhoFor(EventLogEntry entry)
    {
        return entry.Action switch
        {
            EventActions.RunOnStart => _texts["Events.Who.RunOn"],
            EventActions.RunOnEnd => _texts["Events.Who.RunOnDone"],
            EventActions.InterlockTrip => _texts["Events.Who.SafetyShutOff"],
            EventActions.GroupStartFailed => _texts["Events.Who.TheGroup"],
            _ => entry.Actor switch
            {
                EventActors.Schedule => _texts["Events.Who.Schedule"],
                EventActors.AutoOff => _texts["Events.Who.AutoOff"],
                EventActors.External => _texts["Events.Who.Outside"],
                _ => _texts["Events.Who.System"],
            },
        };
    }

    private (string Line, string? Target) LineFor(EventLogEntry entry, string? relayName, string? boardName)
    {
        return entry.Action switch
        {
            EventActions.On => (Line("Events.On"), relayName),
            EventActions.Off when entry.Actor == EventActors.AutoOff => (Line("Events.OffAfter", Duration(Number(entry.Details, "afterMinutes"))), relayName),
            EventActions.Off => (Line("Events.Off"), relayName),
            EventActions.Pulse => (Line("Events.Pulse", PulseLength(Number(entry.Details, "milliseconds"))), relayName),
            EventActions.Rename => (Line("Events.Rename", Text(entry.Details, "from") ?? relayName ?? ""), Text(entry.Details, "to") ?? relayName),
            EventActions.Activate => (Line("Events.Activate"), relayName),
            EventActions.Deactivate => (Line("Events.Deactivate"), relayName),
            EventActions.PowerOnSet => (PowerOnLine(entry.Details), relayName),
            EventActions.ScheduleAdd => (WithSchedule("Events.Schedule.Added", "Events.Schedule.AddedOne", entry.Details), relayName),
            EventActions.ScheduleEdit when Bool(entry.Details, "enabled") is false => (WithSchedule("Events.Schedule.Paused", "Events.Schedule.PausedOne", entry.Details), relayName),
            EventActions.ScheduleEdit when Bool(entry.Details, "enabled") is true => (WithSchedule("Events.Schedule.Resumed", "Events.Schedule.ResumedOne", entry.Details), relayName),
            EventActions.ScheduleEdit => (WithSchedule("Events.Schedule.Changed", "Events.Schedule.ChangedOne", entry.Details), relayName),
            EventActions.ScheduleDelete => (WithSchedule("Events.Schedule.Deleted", "Events.Schedule.DeletedOne", entry.Details), relayName),
            EventActions.ConnectionLost => (Line("Events.Board.Lost"), null),
            EventActions.ConnectionRestored => (Line("Events.Board.Restored"), null),
            EventActions.BoardTimersDisabled => (Line("Events.Board.TimersOff"), null),
            // No longer written; kept for events logged before Issue #497.
            EventActions.BoardRelayModesReset => (Line("Events.Board.ModesReset"), null),
            EventActions.BoardRelayModesNotReset => (Line("Events.Board.ModesNotReset"), null),
            EventActions.BoardOwnRulesTurnedOff => (OwnRulesLine("Events.OwnRules.TurnedOff", entry, boardName), null),
            EventActions.BoardOwnRuleKept => (OwnRulesLine("Events.OwnRules.Kept", entry, boardName), relayName),
            EventActions.BoardLoginChanged => (Line("Events.Board.Login"), null),
            EventActions.BoardRenamed => Text(entry.Details, "from") is { } oldName
                ? (Line("Events.Board.RenamedFrom", oldName, Text(entry.Details, "to") ?? _texts["Events.Unknown"]), null)
                : (Line("Events.Board.Renamed", Text(entry.Details, "to") ?? _texts["Events.Unknown"]), null),
            EventActions.BoardRemoved => (BoardRemoved(entry.Details), null),
            EventActions.BoardAddressChanged => Text(entry.Details, "from") is { } from
                ? (Line("Events.Board.AddressFrom", from, Text(entry.Details, "to") ?? _texts["Events.Unknown"]), null)
                : (Line("Events.Board.Address", Text(entry.Details, "to") ?? _texts["Events.Unknown"]), null),
            _ => (Line("Events.Other", entry.Action), relayName),
        };
    }

    // "set the relay to turn on after a power cut", by the state saved; one this version doesn't know in plain words.
    private string PowerOnLine(string? details)
    {
        if (!PowerOnStateNames.TryParse(Text(details, "to"), out var state))
        {
            return Line("Events.PowerOn.Changed");
        }

        return Line(state switch
        {
            PowerOnState.Off => "Events.PowerOn.Off",
            PowerOnState.AsBefore => "Events.PowerOn.AsBefore",
            PowerOnState.On => "Events.PowerOn.On",
            _ => "Events.PowerOn.FollowsInput",
        });
    }

    // "removed relay board Garage (Velleman VM204)", from what the event saved: the board's row is gone.
    private string BoardRemoved(string? details)
    {
        var model = Text(details, "model") is { Length: > 0 } saved ? saved : null;
        var name = Text(details, "name") is { Length: > 0 } named ? named : null;
        return name is not null && model is not null
            ? Line("Events.Board.Removed", name, model)
            : Line("Events.Board.RemovedNoModel", name ?? model ?? _texts["Events.Unknown"]);
    }

    // "added schedule Mon–Fri 06:30–08:00", or the plain words when the details can't be read.
    private string WithSchedule(string key, string fallbackKey, string? details)
    {
        var days = Number(details, "days");
        var on = Text(details, "on");
        var off = Text(details, "off");
        if (days is not (> 0 and <= (int)ScheduleDays.All)
            || !TimeOnly.TryParseExact(on, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var onTime)
            || !TimeOnly.TryParseExact(off, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var offTime))
        {
            return Line(fallbackKey);
        }

        return Line(key, _schedules.Inline((ScheduleDays)days.Value, onTime, offTime));
    }

    // The line of an event: the text of key with "{0}" kept where the name goes, and the values after it. A value's own
    // braces are doubled, so a name like "{0}" never becomes a place for the name.
    private string Line(string key, params string[] values)
    {
        object[] arguments = ["{0}", .. values.Select(v => v.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal))];
        return _texts[key, arguments];
    }

    /// <summary>When a "pulse" event's pulse ended: the board turned the relay off by itself then.</summary>
    public static DateTime PulseEndUtc(EventLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Number(entry.Details, "milliseconds") is > 0 and var milliseconds
            ? entry.TimestampUtc.AddMilliseconds(milliseconds)
            : entry.TimestampUtc;
    }

    // As Seconds, and "0.5 s" for a pulse that isn't whole seconds.
    private string PulseLength(int? milliseconds)
    {
        return milliseconds switch
        {
            null or <= 0 => _texts["Events.Length.AMoment"],
            _ when milliseconds % 1000 == 0 => Seconds(milliseconds / 1000),
            _ => _texts["Events.Length.Seconds", (milliseconds.Value / 1000.0).ToString("0.#", CultureInfo.CurrentCulture)],
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
