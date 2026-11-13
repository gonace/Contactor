namespace Contactor.Web.Persistence;

/// <summary>
/// A person let in by the sign-in.
/// </summary>
public sealed class User
{
    public const int EmailMaxLength = 254;
    public const int DisplayNameMaxLength = 40;

    /// <summary>Lower case.</summary>
    public string Email { get; set; } = "";

    /// <summary>"John", "Jane" …, shown in the app and in the event log.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>The person's "Debugging" setting: the board's traffic as toasts and the Debug tab. Off by default.</summary>
    public bool ShowBoardTraffic { get; set; }
}
