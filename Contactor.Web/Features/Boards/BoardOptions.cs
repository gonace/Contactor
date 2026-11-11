namespace Contactor.Web.Features.Boards;

/// <summary>The <c>Board</c> configuration section.</summary>
public sealed class BoardOptions
{
    public const string SectionName = "Board";

    /// <summary>Which model of board, by its id in the <see cref="BoardCatalogue"/>, e.g. <c>vm201</c>.</summary>
    public string Type { get; set; } = BoardCatalogue.DefaultType;
}
