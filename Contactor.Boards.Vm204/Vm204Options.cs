using System.ComponentModel.DataAnnotations;

namespace Contactor.Boards.Vm204;

/// <summary>The <c>Vm204</c> configuration section.</summary>
public sealed class Vm204Options
{
    public const string SectionName = "Vm204";

    public string Host { get; set; } = "";

    [Range(1, 65535)]
    public int Port { get; set; } = Vm204Api.DefaultPort;

    /// <summary>The login of the board's web page (factory setting: admin).</summary>
    [MaxLength(Vm204Api.CredentialMaxLength)]
    public string Username { get; set; } = "";

    /// <summary>A secret: never log it, never commit it.</summary>
    [MaxLength(Vm204Api.CredentialMaxLength)]
    public string Password { get; set; } = "";

    /// <summary>How often the board is asked for its state; it never reports a change by itself.</summary>
    [Range(1, 60)]
    public int PollSeconds { get; set; } = 2;
}
