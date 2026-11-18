using Contactor.Boards;

namespace Contactor.Web.Persistence;

/// <summary>What an <see cref="EventLogEntry"/> records.</summary>
public static class EventActions
{
    public const string On = "on";
    public const string Off = "off";

    /// <summary>A pulse: on, and off again by the board itself. Details: <c>{"milliseconds":2000}</c>.</summary>
    public const string Pulse = "pulse";
    public const string Rename = "rename";
    public const string Activate = "activate";
    public const string Deactivate = "deactivate";
    public const string ScheduleAdd = "schedule-add";
    public const string ScheduleEdit = "schedule-edit";
    public const string ScheduleDelete = "schedule-delete";
    public const string ConnectionLost = "connection-lost";
    public const string ConnectionRestored = "connection-restored";
    public const string InputChanged = "input-changed";
    /// <summary>No longer written; kept for events logged before Issue #497.</summary>
    public const string BoardTimersDisabled = "board-timers-disabled";

    /// <summary>No longer written; kept for events logged before Issue #497.</summary>
    public const string BoardRelayModesReset = "board-relay-modes-reset";

    /// <summary>No longer written; kept for events logged before Issue #497.</summary>
    public const string BoardRelayModesNotReset = "board-relay-modes-not-reset";

    /// <summary>
    /// The app saw a rule of the board's own (a <c>BoardOwnRule</c>), logged by "system" with the relay. Details:
    /// <c>{"kind":"timer","new":true,"relay":3}</c>: <c>new</c> when it came while the app watched the board, <c>relay</c>
    /// its channel.
    /// </summary>
    public const string BoardOwnRuleFound = "board-own-rule-found";

    /// <summary>A person turned off rules of the board's own. Details: <c>{"relays":[3,5],"kinds":["timer"]}</c>, by channel.</summary>
    public const string BoardOwnRulesTurnedOff = "board-own-rules-turned-off";

    /// <summary>A person kept a rule of the board's own, with the relay. Details: <c>{"kind":"timer","relay":3}</c>.</summary>
    public const string BoardOwnRuleKept = "board-own-rule-kept";

    /// <summary>
    /// A person set what the relay does by itself when its board gets power back (Issue #516), with the relay. Details:
    /// <c>{"from":"off","to":"on"}</c>, as <see cref="PowerOnStateNames"/> saves them; no <c>from</c> when it was unknown.
    /// </summary>
    public const string PowerOnSet = "power-on-set";
    public const string BoardAddressChanged = "board-address-changed";
    public const string BoardLoginChanged = "board-login-changed";

    /// <summary>A board's new name. Details: <c>{"from":"Barn","to":"Garage"}</c>.</summary>
    public const string BoardRenamed = "board-renamed";

    /// <summary>
    /// A board taken out of the app, logged with no board so it outlives the board's own events. Details:
    /// <c>{"name":"Garage","model":"Velleman VM204"}</c>, as they were at the removal; no model for a board without one.
    /// </summary>
    public const string BoardRemoved = "board-removed";
    public const string GroupOn = "group-on";
    public const string GroupOff = "group-off";
    public const string GroupStartFailed = "group-start-failed";
    public const string RunOnStart = "run-on-start";
    public const string RunOnEnd = "run-on-end";
    public const string InterlockTrip = "interlock-trip";
    public const string GroupCreate = "group-create";
    public const string GroupEdit = "group-edit";
    public const string GroupDelete = "group-delete";

    /// <summary>"Update now" in Settings. Details: <c>{"from":"2026.11.14","to":"2026.11.20"}</c>.</summary>
    public const string UpdateRequested = "update-requested";

    /// <summary>An update that went through. Details: <c>{"id":"20261115-140200","from":…,"to":…}</c>, the id naming its log.</summary>
    public const string UpdateDone = "update-done";

    /// <summary>An update whose new version didn't come up: Contactor went back to the one before. Details as for <see cref="UpdateDone"/>, with <c>reason</c>.</summary>
    public const string UpdateWentBack = "update-went-back";

    /// <summary>An update that stopped before the restart: nothing was changed. Details as for <see cref="UpdateWentBack"/>.</summary>
    public const string UpdateFailed = "update-failed";

    /// <summary>The helper found nothing newer to download than what runs. Details as for <see cref="UpdateDone"/>.</summary>
    public const string UpdateCurrent = "update-current";
}

/// <summary>The states after a power cut as the events save them, from <see cref="PowerOnState"/>.</summary>
public static class PowerOnStateNames
{
    public const string Off = "off";
    public const string AsBefore = "as-before";
    public const string On = "on";
    public const string FollowsInput = "follows-input";

    /// <summary>The state as it is saved.</summary>
    public static string For(PowerOnState state)
    {
        return state switch
        {
            PowerOnState.Off => Off,
            PowerOnState.AsBefore => AsBefore,
            PowerOnState.On => On,
            PowerOnState.FollowsInput => FollowsInput,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a state after a power cut."),
        };
    }

    /// <summary>The state a saved text names; false for one this version doesn't know.</summary>
    public static bool TryParse(string? text, out PowerOnState state)
    {
        switch (text)
        {
            case Off:
                state = PowerOnState.Off;
                return true;
            case AsBefore:
                state = PowerOnState.AsBefore;
                return true;
            case On:
                state = PowerOnState.On;
                return true;
            case FollowsInput:
                state = PowerOnState.FollowsInput;
                return true;
            default:
                state = default;
                return false;
        }
    }
}
