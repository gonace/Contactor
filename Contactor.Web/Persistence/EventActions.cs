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
    public const string BoardAddressChanged = "board-address-changed";
    public const string BoardLoginChanged = "board-login-changed";
    public const string GroupOn = "group-on";
    public const string GroupOff = "group-off";
    public const string GroupStartFailed = "group-start-failed";
    public const string RunOnStart = "run-on-start";
    public const string RunOnEnd = "run-on-end";
    public const string InterlockTrip = "interlock-trip";
    public const string GroupCreate = "group-create";
    public const string GroupEdit = "group-edit";
    public const string GroupDelete = "group-delete";
}
