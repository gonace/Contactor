namespace Contactor.Simulator;

/// <summary>
/// The page's fields for a Shelly's controls: what is typed and the errors shown. They belong to the board the page
/// shows, so they start afresh whenever it shows another, also when the board it showed is removed.
/// </summary>
public sealed class ShellyForm
{
    public const string On = "on";
    public const string Off = "off";
    public const string DefaultTime = "08:00";

    /// <summary>The board the fields are for; null when none is shown.</summary>
    public int? BoardId { get; private set; }

    public string? FirmwareError { get; set; }

    public string? ScheduleError { get; set; }

    public int ScheduleRelay { get; set; } = 1;

    /// <summary>"on" or "off", as the select's options.</summary>
    public string ScheduleOn { get; set; } = On;

    /// <summary>The relay from the select's value; anything but a number is ignored, and the controls check the range.</summary>
    public void SetScheduleRelay(string? value)
    {
        if (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var relay))
        {
            ScheduleRelay = relay;
        }
    }

    /// <summary>"on" or "off" from the select's value; anything else is ignored.</summary>
    public void SetScheduleOn(string? value)
    {
        if (value is On or Off)
        {
            ScheduleOn = value;
        }
    }

    public string? ScheduleTime { get; set; } = DefaultTime;

    /// <summary>The new password typed; cleared once the device takes it.</summary>
    public string NewPassword { get; set; } = "";

    public string? PasswordError { get; set; }

    /// <summary>Starts the fields afresh when <paramref name="boardId"/> is another board than the one they are for.</summary>
    public void Follow(int? boardId)
    {
        if (boardId == BoardId)
        {
            return;
        }

        BoardId = boardId;
        FirmwareError = null;
        ScheduleError = null;
        ScheduleRelay = 1;
        ScheduleOn = On;
        ScheduleTime = DefaultTime;
        NewPassword = "";
        PasswordError = null;
    }

    public void SetFirmware(ShellyControls shelly, string? firmware)
    {
        ArgumentNullException.ThrowIfNull(shelly);
        FirmwareError = shelly.SetFirmware(firmware);
    }

    /// <summary>Adds the schedule typed; "on" turns the relay on, anything else off.</summary>
    public void AddSchedule(ShellyControls shelly)
    {
        ArgumentNullException.ThrowIfNull(shelly);
        ScheduleError = shelly.AddSchedule(ScheduleRelay, ScheduleOn == On, ScheduleTime);
    }

    /// <summary>Changes the device's password to the one typed.</summary>
    public void ChangePassword(ShellyControls shelly)
    {
        ArgumentNullException.ThrowIfNull(shelly);
        PasswordError = shelly.ChangePassword(NewPassword);
        if (PasswordError is null)
        {
            NewPassword = "";
        }
    }
}
