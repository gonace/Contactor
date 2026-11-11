using System.ComponentModel.DataAnnotations;

namespace Contactor.Boards.Vm201;

/// <summary>
/// The <c>Vm201</c> configuration section.
/// </summary>
public sealed class Vm201Options
{
    public const string SectionName = "Vm201";

    public string Host { get; set; } = "";

    [Range(1, 65535)]
    public int Port { get; set; } = 9760;

    /// <summary>Empty when the board does not require a login.</summary>
    [MaxLength(Vm201Command.CredentialMaxLength)]
    public string Username { get; set; } = "";

    /// <summary>A secret: never log it, never commit it.</summary>
    [MaxLength(Vm201Command.CredentialMaxLength)]
    public string Password { get; set; } = "";

    /// <summary>Disable the board's own timer programs on the first connection, so they don't fight the app's schedules.</summary>
    public bool DisableBoardTimers { get; set; } = true;

    /// <summary>
    /// Logs every packet to and from the board, as hex with its command letter, to see how a real board behaves.
    /// The user name and password are never written out. Off by default: with the keep-alive it is a line every 15 s.
    /// </summary>
    public bool LogPackets { get; set; }

    /// <summary>
    /// How often a status request is sent to keep the connection alive. With no status for twice this time the
    /// connection is considered dead and reopened.
    /// </summary>
    [Range(1, 300)]
    public int KeepAliveSeconds { get; set; } = 15;
}
