using System.ComponentModel.DataAnnotations;

namespace Contactor.Boards.Devantech;

/// <summary>The <c>Devantech</c> configuration section, for all the Devantech models.</summary>
public sealed class DevantechOptions
{
    public const string SectionName = "Devantech";

    public string Host { get; set; } = "";

    [Range(1, 65535)]
    public int Port { get; set; } = DevantechApi.DefaultPort;

    /// <summary>The board's TCP password; empty when it has none. A secret: never log it.</summary>
    public string Password { get; set; } = "";

    /// <summary>How often the board is asked for its state; it never reports a change by itself.</summary>
    [Range(1, 60)]
    public int PollSeconds { get; set; } = 2;
}
