using System.ComponentModel.DataAnnotations;

namespace Contactor.Boards.DAEnetIP2v2;

/// <summary>Settings shared by both DAEnetIP2v2 relay kits.</summary>
public sealed class DAEnetIP2v2Options
{
    public const string SectionName = "DAEnetIP2v2";
    public string Host { get; set; } = "";
    [Range(1, 65535)] public int Port { get; set; } = DAEnetIP2v2Models.DefaultPort;
    [Range(1, 60)] public int PollSeconds { get; set; } = 2;
    [StringLength(64)] public string Username { get; set; } = "";
    /// <summary>A secret: never log it.</summary>
    [StringLength(64)] public string Password { get; set; } = "";
}
