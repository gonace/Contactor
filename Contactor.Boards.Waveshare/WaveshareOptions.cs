using System.ComponentModel.DataAnnotations;

namespace Contactor.Boards.Waveshare;

/// <summary>
/// The <c>Waveshare</c> configuration section, for all the Waveshare models. There is no login: Modbus has none.
/// </summary>
public sealed class WaveshareOptions
{
    public const string SectionName = "Waveshare";

    public string Host { get; set; } = "";

    [Range(1, 65535)]
    public int Port { get; set; } = WaveshareApi.DefaultPort;

    /// <summary>How often the board is asked for its state; it never reports a change by itself.</summary>
    [Range(1, 60)]
    public int PollSeconds { get; set; } = 2;
}
