using System.Globalization;

namespace Contactor.Web.Features.Relays;

/// <summary>
/// A screen with a board filter, with two or more boards: Relays, Events and Debug, each remembered on its own.
/// </summary>
/// <param name="CookieName">The cookie that keeps the screen's picked boards: their ids, e.g. <c>1-3</c>.</param>
/// <param name="FormPath">Where the screen's filter is sent (<see cref="RelaysEndpoints"/>).</param>
/// <param name="PagePath">The screen, where the form goes back to.</param>
public sealed record BoardFilterScreen(string CookieName, string FormPath, string PagePath)
{
    public static BoardFilterScreen Relays { get; } = new(BoardFilter.CookieName, RelaysEndpoints.BoardsPath, "/");

    public static BoardFilterScreen Events { get; } = new("contactor-events-boards", "/events/boards", "/events");

    public static BoardFilterScreen Debug { get; } = new("contactor-debug-boards", "/debug/boards", "/debug");

    /// <summary>Every screen with a board filter.</summary>
    public static IReadOnlyList<BoardFilterScreen> All { get; } = [Relays, Events, Debug];
}

/// <summary>
/// Each screen's board filter (<see cref="BoardFilterScreen"/>), as this browser's cookies had them when the page was
/// opened.
/// </summary>
public sealed record BoardFilters(BoardFilter Relays, BoardFilter Events, BoardFilter Debug)
{
    public static BoardFilters All { get; } = new(BoardFilter.All, BoardFilter.All, BoardFilter.All);
}

/// <summary>
/// Which boards a screen shows, with two or more boards: all of them (<see cref="All"/>), or the ones picked.
/// Remembered per browser and screen in a cookie (<see cref="BoardFilterScreen.CookieName"/>), set by
/// <see cref="RelaysEndpoints"/> and read on the server when the page is made, so the page never shows every board
/// first. A board that is gone is left out; when no picked board is left, every board shows.
/// </summary>
public sealed class BoardFilter
{
    /// <summary>The cookie that keeps the Relays screen's picked boards: their ids, e.g. <c>1-3</c>.</summary>
    public const string CookieName = "contactor-relays-boards";

    /// <summary>What a form sends to show every board again.</summary>
    public const string AllValue = "all";

    // More than any installation has; a longer cookie is ignored rather than read.
    private const int MaxBoards = 64;
    private const int MaxLength = 400;

    private readonly HashSet<int> _picked;

    private BoardFilter(IEnumerable<int> picked)
    {
        _picked = [.. picked];
    }

    /// <summary>Every board.</summary>
    public static BoardFilter All { get; } = new([]);

    /// <summary>Whether every board shows.</summary>
    public bool IsAll => _picked.Count == 0;

    /// <summary>The picked boards' ids; empty for every board.</summary>
    public IReadOnlySet<int> Picked => _picked;

    /// <summary>
    /// The filter a cookie or a form holds: board ids joined by <c>-</c>. Anything else (empty, <see cref="AllValue"/>,
    /// too long, not a positive number) is every board.
    /// </summary>
    public static BoardFilter Parse(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return All;
        }

        var ids = new HashSet<int>();
        foreach (var part in value.Split('-'))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                return All;
            }

            ids.Add(id);
            if (ids.Count > MaxBoards)
            {
                return All;
            }
        }

        return new BoardFilter(ids);
    }

    /// <summary>The cookie's value: the ids in order, joined by <c>-</c>; empty for every board.</summary>
    public string Format()
    {
        return string.Join('-', _picked.Order().Select(id => id.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// The filter as it applies to the boards there are now: only boards that exist, and every board when none of the
    /// picked ones is left, or every one is picked.
    /// </summary>
    public BoardFilter For(IReadOnlyCollection<int> boardIds)
    {
        ArgumentNullException.ThrowIfNull(boardIds);
        var left = _picked.Where(boardIds.Contains).ToList();
        return left.Count == 0 || left.Count == boardIds.Count ? All : new BoardFilter(left);
    }

    /// <summary>Whether the board shows.</summary>
    public bool Shows(int boardId)
    {
        return IsAll || _picked.Contains(boardId);
    }

    /// <summary>
    /// The filter after a tap on a board: from every board, only that board; a picked board is let go (every board when
    /// it was the last); another board is added (every board once all are).
    /// </summary>
    public BoardFilter Toggle(int boardId, IReadOnlyCollection<int> boardIds)
    {
        ArgumentNullException.ThrowIfNull(boardIds);
        var now = For(boardIds);
        if (now.IsAll)
        {
            return new BoardFilter([boardId]).For(boardIds);
        }

        var picked = new HashSet<int>(now._picked);
        if (!picked.Remove(boardId))
        {
            picked.Add(boardId);
        }

        return new BoardFilter(picked).For(boardIds);
    }
}
