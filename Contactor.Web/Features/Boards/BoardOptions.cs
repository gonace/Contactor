namespace Contactor.Web.Features.Boards;

/// <summary>
/// The <c>Board</c> configuration section, read only once, for an installation in use from before the setup guide
/// chose the model (<see cref="BoardChoice.ApplySavedAsync"/>). A new installation chooses its board in the guide.
/// </summary>
public sealed class BoardOptions
{
    public const string SectionName = "Board";

    /// <summary>Which model of board, by its id in the <see cref="BoardCatalogue"/>, e.g. <c>vm201</c>.</summary>
    public string? Type { get; set; }
}
