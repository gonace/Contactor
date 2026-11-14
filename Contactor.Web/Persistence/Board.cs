namespace Contactor.Web.Persistence;

/// <summary>
/// A relay board the app runs: its model, where it is and its login. One for now; several at once is issue #4. Its
/// relays and inputs are rows of their own (<see cref="Relay.BoardId"/>, <see cref="Input.BoardId"/>), and what the app
/// keeps about it while it runs is in <see cref="BoardRuntime"/>.
/// </summary>
public sealed class Board
{
    public const int NameMaxLength = 40;
    public const int TypeMaxLength = 32;
    public const int HostMaxLength = 253;

    public int Id { get; set; }

    /// <summary>What people call it, e.g. "Garage". The model's name to begin with.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The model, by its id in the board catalogue (e.g. <c>vm201</c>). Empty while none is chosen: a board whose rows
    /// were there before the setup guide chose the model, which <c>BoardChoice</c> gives one at start.
    /// </summary>
    public string Type { get; set; } = "";

    /// <summary>
    /// The address set in the app (the Settings screen or the setup guide): an IP address or a host name. Null, with
    /// <see cref="Port"/>, while the board's own settings apply (what install.sh asked for, e.g. <c>Vm201:Host</c>).
    /// </summary>
    public string? Host { get; set; }

    /// <summary>1–65535; null with <see cref="Host"/>.</summary>
    public int? Port { get; set; }

    /// <summary>The board's user name; null while the login from the board's own settings applies.</summary>
    public string? Username { get; set; }

    /// <summary>The board's password, kept as typed because the board needs it so. A secret: never log or show it.</summary>
    public string? Password { get; set; }

    public int SortOrder { get; set; }

    /// <summary>The name a board gets from its model, until someone gives it one: "Velleman VM201".</summary>
    public static string DefaultName(string modelName)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        return modelName.Length <= NameMaxLength ? modelName : modelName[..NameMaxLength];
    }
}
