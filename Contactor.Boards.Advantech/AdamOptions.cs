using System.ComponentModel.DataAnnotations;

namespace Contactor.Boards.Advantech;

/// <summary>The <c>Advantech</c> configuration section, for the ADAM modules. There is no login: Modbus has none.</summary>
public sealed class AdamOptions
{
    public const string SectionName = "Advantech";

    public string Host { get; set; } = "";

    [Range(1, 65535)]
    public int Port { get; set; } = AdamApi.DefaultPort;

    /// <summary>How often the module is asked for its state; it never reports a change by itself.</summary>
    [Range(1, 60)]
    public int PollSeconds { get; set; } = 2;
}
