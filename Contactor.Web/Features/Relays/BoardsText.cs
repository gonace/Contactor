using Contactor.Web.Features.Languages;
using Contactor.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Contactor.Web.Features.Relays;

/// <summary>The words about the boards on Relays, Events and Debug, with two or more boards.</summary>
public sealed class BoardsText
{
    private readonly IStringLocalizer<Texts> _texts;

    public BoardsText(IStringLocalizer<Texts> texts)
    {
        _texts = texts;
    }

    /// <summary>"Turn off all 16 relays on 9 boards?": what "Turn off all boards" asks first.</summary>
    /// <param name="relaysOn">The relays on now, on every board.</param>
    /// <param name="boards">How many boards there are.</param>
    public string ConfirmAllOff(int relaysOn, int boards)
    {
        var where = _texts.Plural("Relays.OnBoards", boards);
        return relaysOn switch
        {
            <= 0 => _texts["Relays.ConfirmAllOff.Every", where],
            1 => _texts["Relays.ConfirmAllOff.One", where],
            _ => _texts["Relays.ConfirmAllOff.All", where, relaysOn],
        };
    }

    /// <summary>
    /// "Couldn't reach Garage and Barn. Their relays are as they were.": the boards "Turn off all boards" couldn't
    /// reach; null when it reached every board.
    /// </summary>
    public string? Unreachable(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Count switch
        {
            0 => null,
            1 => _texts["Relays.Unreachable.One", names[0]].Value,
            _ => _texts["Relays.Unreachable.Several", And(names)].Value,
        };
    }

    /// <summary>"Showing 2 of 9 boards · 7 of 28 on", while the screen shows only some boards.</summary>
    public string Showing(int shown, int boards, int on, int active)
    {
        return _texts["Relays.Showing.Boards", shown, boards, on, active];
    }

    /// <summary>"Showing Workshop and Paint shop", while Events or Debug shows only some boards.</summary>
    public string Showing(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return _texts["Relays.Showing.Names", And(names)];
    }

    /// <summary>A list in words: "Garage", "Garage and Barn", "Garage, Barn and Office".</summary>
    public string And(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Count <= 1
            ? string.Concat(names)
            : _texts["Relays.List.Last", string.Join(_texts["Relays.List.Separator"], names.Take(names.Count - 1)), names[^1]];
    }
}
