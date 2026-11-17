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
    public const string BoardTimersDisabled = "board-timers-disabled";
    public const string BoardRelayModesReset = "board-relay-modes-reset";
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
