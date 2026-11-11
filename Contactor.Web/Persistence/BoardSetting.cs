namespace Contactor.Web.Persistence;

/// <summary>
/// The relay board's address and login as set on the Settings screen. One row; while it doesn't exist (or its
/// login is null), the app uses the board's own settings (what install.sh asked for, e.g. <c>Vm201:Host</c>,
/// <c>Port</c>, <c>Username</c> and <c>Password</c>).
/// </summary>
public sealed class BoardSetting
{
    public const int SingletonId = 1;
    public const int HostMaxLength = 253;

    public int Id { get; set; } = SingletonId;

    public string Host { get; set; } = "";

    public int Port { get; set; }

    /// <summary>The board's user name; null while the login from the settings (install.sh) applies.</summary>
    public string? Username { get; set; }

    /// <summary>The board's password, kept as typed because the board needs it so. A secret: never log it.</summary>
    public string? Password { get; set; }
}
