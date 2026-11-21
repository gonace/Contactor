namespace Contactor.Boards;

/// <summary>What a kind of board has and does.</summary>
/// <param name="RelayCount">How many relays, numbered 1 to this.</param>
/// <param name="InputCount">
/// How many digital inputs, numbered 1 to this. With <paramref name="InputCountFromBoard"/>, in the catalogue the most
/// the board may have, and on a live board (<see cref="IRelayBoard.Capabilities"/>) how many it said it has.
/// </param>
/// <param name="ReportsChanges">
/// Whether the board reports every change by itself, including ones made at the board. Without it the app has to
/// ask, and changes made elsewhere show later.
/// </param>
/// <param name="Analog">Its analog inputs and what their readings mean; null when it has none.</param>
/// <param name="Pulse">
/// Whether, and how long, the board can pulse a relay (<see cref="IRelayBoard.PulseAsync"/>): turn it on, and off again
/// by itself after a time, without the app. Null: it can't.
/// </param>
/// <param name="OwnRules">
/// What the app can know and do about the board's own rules, which switch relays without it. Every model in the
/// catalogue states it; null means "can't tell" (<see cref="OwnRulesOrCantTell"/>), so a board that says nothing is
/// never taken to have none.
/// </param>
/// <param name="PowerOn">
/// What the board can do about its relays' state after a power cut: which states it has, per relay or for the whole
/// board. Null means "can't tell": the app can neither read nor set it, and the board may still switch relays by itself
/// when the power comes back. A model in the catalogue that leaves it null does so on purpose.
/// </param>
/// <param name="InputCountFromBoard">
/// Whether the board says at each connection how many digital inputs it has, e.g. a Shelly, whose models with the same
/// number of relays have different inputs. The catalogue's <paramref name="InputCount"/> is then the most it may have,
/// and the app keeps a row for each; the live board's is how many it said on its last connection, 0 before it answered,
/// and the app shows only those, as it hides the rows a board with more inputs left behind.
/// </param>
public sealed record BoardCapabilities(int RelayCount, int InputCount, bool ReportsChanges, AnalogAbility? Analog = null, PulseAbility? Pulse = null, OwnRulesAbility? OwnRules = null, PowerOnAbility? PowerOn = null, bool InputCountFromBoard = false)
{
    /// <summary>What the app can know and do about the board's own rules: <see cref="OwnRulesAbility.CantTell"/> when it doesn't say.</summary>
    public OwnRulesAbility OwnRulesOrCantTell => OwnRules ?? OwnRulesAbility.CantTell;

    /// <summary>How many analog inputs it has, numbered 1 to this.</summary>
    public int AnalogInputCount => Analog?.Count ?? 0;

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

/// <summary>
/// A board's analog inputs: <paramref name="Count"/> of them, each read by a converter that gives whole counts from 0 to
/// <paramref name="MaxCounts"/>, where <paramref name="MaxCounts"/> means <paramref name="FullScaleVolts"/> at the
/// terminal. A reading at <paramref name="MaxCounts"/> is the top of the range: the voltage may be higher.
/// </summary>
public sealed record AnalogAbility(int Count, int MaxCounts, decimal FullScaleVolts)
{
    /// <summary>How many analog inputs, numbered 1 to this.</summary>
    public int Count { get; } = Count >= 1 ? Count : throw new ArgumentOutOfRangeException(nameof(Count), Count, "A board with analog inputs has at least one.");

    /// <summary>The highest count the converter gives, e.g. 1023 for 10 bits.</summary>
    public int MaxCounts { get; } = MaxCounts >= 1 ? MaxCounts : throw new ArgumentOutOfRangeException(nameof(MaxCounts), MaxCounts, "The highest count is at least 1.");

    /// <summary>The voltage at the terminal that <see cref="MaxCounts"/> means.</summary>
    public decimal FullScaleVolts { get; } = FullScaleVolts > 0m ? FullScaleVolts : throw new ArgumentOutOfRangeException(nameof(FullScaleVolts), FullScaleVolts, "The full scale is more than 0 V.");

    /// <summary>The voltage one count stands for.</summary>
    public decimal VoltsPerCount => FullScaleVolts / MaxCounts;

    /// <summary>
    /// The voltage at the terminal for a reading, e.g. 6.6 V for 1023 on the VM204. Meant for a reading the converter can
    /// give (<see cref="Check"/>); any other count is only scaled, without complaint, so a value the screens show must
    /// pass <see cref="Check"/> first.
    /// </summary>
    public decimal Volts(int counts)
    {
        // Multiplied first, so the full scale comes out exactly.
        return counts * FullScaleVolts / MaxCounts;
    }

    /// <summary>The reading when it is one the converter can give (0 to <see cref="MaxCounts"/>), otherwise null.</summary>
    public int? Check(int? counts)
    {
        return counts is { } value && value >= 0 && value <= MaxCounts ? value : null;
    }

    /// <summary>
    /// Whether a reading is the top of the range, where the voltage may be higher than the board can read. Meant for a
    /// reading <see cref="Check"/> passed: a count above <see cref="MaxCounts"/> gives false, without complaint.
    /// </summary>
    public bool IsFullScale(int counts)
    {
        return counts == MaxCounts;
    }
}
