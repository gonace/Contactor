using System.ComponentModel.DataAnnotations;

namespace Contactor.Boards.Denkovi;

/// <summary>The <c>DaenetIp2</c> configuration section, for the DAEnetIP2 v2 kits.</summary>
public sealed class DaenetIp2Options
{
    public const string SectionName = "DaenetIp2";

    public string Host { get; set; } = "";

    /// <summary>The SNMP agent's UDP port.</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = DaenetIp2Api.DefaultPort;

    /// <summary>The board's read-write community, its only login; a secret, never logged. Empty: none saved yet.</summary>
    public string Community { get; set; } = "";

    /// <summary>How often the board is asked for its state; it never reports a change by itself.</summary>
    [Range(1, 60)]
    public int PollSeconds { get; set; } = 2;
}
