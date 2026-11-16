using System.ComponentModel.DataAnnotations;

namespace Contactor.Boards.Denkovi;

/// <summary>The <c>SmartDen</c> configuration section, for the smartDEN IP-16R-MT. There is no login: Modbus has none.</summary>
public sealed class SmartDenOptions
{
    public const string SectionName = "SmartDen";

    public string Host { get; set; } = "";

    [Range(1, 65535)]
    public int Port { get; set; } = SmartDenApi.DefaultPort;

    /// <summary>How often the board is asked for its state; it never reports a change by itself.</summary>
    [Range(1, 60)]
    public int PollSeconds { get; set; } = 2;
}
