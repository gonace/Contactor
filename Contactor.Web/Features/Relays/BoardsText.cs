namespace Contactor.Web.Features.Relays;

/// <summary>The words about the boards on Relays, Events and Debug, with two or more boards.</summary>
public static class BoardsText
{
    /// <summary>"Turn off all 16 relays on 9 boards?": what "Turn off all boards" asks first.</summary>
    /// <param name="relaysOn">The relays on now, on every board.</param>
    /// <param name="boards">How many boards there are.</param>
    public static string ConfirmAllOff(int relaysOn, int boards)
    {
        var where = boards == 1 ? "on 1 board" : $"on {boards} boards";
        return relaysOn switch
        {
            <= 0 => $"Turn off every relay {where}?",
            1 => $"Turn off the 1 relay that is on, {where}?",
            _ => $"Turn off all {relaysOn} relays {where}?",
        };
    }

    /// <summary>
    /// "Couldn't reach Garage and Barn. Their relays are as they were.": the boards "Turn off all boards" couldn't
    /// reach; null when it reached every board.
    /// </summary>
    public static string? Unreachable(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Count switch
        {
            0 => null,
            1 => $"Couldn't reach {names[0]}. Its relays are as they were.",
            _ => $"Couldn't reach {And(names)}. Their relays are as they were.",
        };
    }

    /// <summary>"Showing 2 of 9 boards · 7 of 28 on", while the screen shows only some boards.</summary>
    public static string Showing(int shown, int boards, int on, int active)
    {
        return $"Showing {shown} of {boards} boards · {on} of {active} on";
    }

    /// <summary>"Showing Workshop and Paint shop", while Events or Debug shows only some boards.</summary>
    public static string Showing(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return $"Showing {And(names)}";
    }

    // "Garage", "Garage and Barn", "Garage, Barn and Office".
    private static string And(IReadOnlyList<string> names)
    {
        return names.Count <= 1 ? string.Concat(names) : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
    }
}
