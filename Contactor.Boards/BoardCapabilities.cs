namespace Contactor.Boards;

/// <summary>What a kind of board has and does.</summary>
/// <param name="RelayCount">How many relays, numbered 1 to this.</param>
/// <param name="InputCount">How many digital inputs, numbered 1 to this.</param>
/// <param name="ReportsChanges">
/// Whether the board reports every change by itself, including ones made at the board. Without it the app has to
/// ask, and changes made elsewhere show later.
/// </param>
/// <param name="AnalogInputCount">How many analog inputs it has. The app doesn't show them yet.</param>
/// <param name="Pulse">
/// Whether, and how long, the board can pulse a relay (<see cref="IRelayBoard.PulseAsync"/>): turn it on, and off again
/// by itself after a time, without the app. Null: it can't.
/// </param>
public sealed record BoardCapabilities(int RelayCount, int InputCount, bool ReportsChanges, int AnalogInputCount = 0, PulseAbility? Pulse = null)
{
    /// <summary>All of the board's relays.</summary>
    public ChannelMask AllRelays => ChannelMask.FirstN(RelayCount);

    /// <summary>One of the board's relays; throws for a number the board doesn't have, before anything is sent.</summary>
    public ChannelMask Relay(int relay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(relay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relay, RelayCount);
        return ChannelMask.Of(relay);
    }
}

/// <summary>The lengths a board can pulse a relay for: from <paramref name="Shortest"/> to <paramref name="Longest"/>, in steps of <paramref name="Step"/>.</summary>
public sealed record PulseAbility(TimeSpan Shortest, TimeSpan Longest, TimeSpan Step)
{
    /// <summary>
    /// For a board whose lengths a range and a step can't say exactly, e.g. the VM201's: whether it can send a length
    /// in the range. Null when the range and the step say it all.
    /// </summary>
    public Func<TimeSpan, bool>? Exact { get; init; }

    /// <summary>Whether the board can pulse for exactly <paramref name="length"/>.</summary>
    public bool CanDo(TimeSpan length) =>
        length >= Shortest && length <= Longest && length.Ticks % Step.Ticks == 0 && (Exact is null || Exact(length));
}
