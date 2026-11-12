using System.ComponentModel.DataAnnotations;

namespace Contactor.Boards.Vm208;

/// <summary>
/// The <c>Vm208</c> configuration section. There is no login: the board's API takes none (see <see cref="Vm208Model"/>).
/// </summary>
public sealed class Vm208Options
{
    public const string SectionName = "Vm208";

    public string Host { get; set; } = "";

    [Range(1, 65535)]
    public int Port { get; set; } = Vm208Api.DefaultPort;

    /// <summary>How often the board is asked for its state; it never reports a change by itself.</summary>
    [Range(1, 60)]
    public int PollSeconds { get; set; } = 2;
}
