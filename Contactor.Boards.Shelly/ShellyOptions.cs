using System.ComponentModel.DataAnnotations;

namespace Contactor.Boards.Shelly;

/// <summary>The <c>Shelly</c> configuration section, for all the Shelly entries.</summary>
public sealed class ShellyOptions
{
    public const string SectionName = "Shelly";

    public string Host { get; set; } = "";

    [Range(1, 65535)]
    public int Port { get; set; } = ShellyApi.DefaultPort;

    /// <summary>The device's password; empty when it has none. A secret: never log it.</summary>
    [MaxLength(ShellyApi.PasswordMaxLength)]
    public string Password { get; set; } = "";

    /// <summary>How often the device is asked for its state; over HTTP it never reports a change by itself.</summary>
    [Range(1, 60)]
    public int PollSeconds { get; set; } = 2;
}
