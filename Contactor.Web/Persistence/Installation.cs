namespace Contactor.Web.Persistence;

/// <summary>
/// The installation itself: which model of board it has, and whether the setup guide has been done. One row; while
/// it doesn't exist, <c>Board:Type</c> chooses the model and the app opens on the setup guide.
/// </summary>
public sealed class Installation
{
    public const int SingletonId = 1;
    public const int BoardTypeMaxLength = 32;

    public int Id { get; set; } = SingletonId;

    /// <summary>
    /// The model chosen in the setup guide, by its id in the board catalogue (e.g. <c>vm201</c>); it wins over
    /// <c>Board:Type</c>. Null while none is chosen.
    /// </summary>
    public string? BoardType { get; set; }

    /// <summary>When the setup guide was finished or skipped; null while the app should open on it.</summary>
    public DateTime? SetupDoneUtc { get; set; }
}
