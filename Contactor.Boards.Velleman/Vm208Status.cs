namespace Contactor.Boards.Velleman;

/// <summary>One answer to a status request: which relays are on, the input, and the two transistor outputs.</summary>
/// <param name="Relays">Bit per relay, bit 0 = relay 1; 1 = on.</param>
/// <param name="Input">The input as the board reports it (<c>true</c> in its JSON, "ON" on its own page).</param>
/// <param name="Outputs">
/// The two MOSFET outputs, bit 0 = <c>m1</c>, bit 1 = <c>m2</c>. They are not relays: the app doesn't count, show or
/// switch them.
/// </param>
/// <param name="OwnRules">
/// The board's own rules as last read (<see cref="Vm208Client.SchedulesInterval"/>): a <see cref="OwnRuleKind.Timer"/>
/// rule on each relay whose week schedule has an enabled alarm; null while the schedules aren't known.
/// </param>
public sealed record Vm208Status(byte Relays, bool Input, byte Outputs, OwnRules? OwnRules = null)
{
    /// <summary>
    /// Compares the relays and the input only: the app doesn't show the transistor outputs, so a change of those
    /// alone is no change worth telling every screen about, and a change of the own rules alone comes as
    /// <see cref="Vm208Client.OwnRulesChanged"/>.
    /// </summary>
    public static IEqualityComparer<Vm208Status> SwitchesComparer { get; } = new Switches();

    /// <summary>Whether the relays and the input are the same as in <paramref name="other"/>.</summary>
    public bool SameSwitches(Vm208Status? other)
    {
        return other is not null && Relays == other.Relays && Input == other.Input;
    }

    private sealed class Switches : IEqualityComparer<Vm208Status>
    {
        public bool Equals(Vm208Status? x, Vm208Status? y)
        {
            return ReferenceEquals(x, y) || (x is not null && x.SameSwitches(y));
        }

        public int GetHashCode(Vm208Status obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            return HashCode.Combine(obj.Relays, obj.Input);
        }
    }
}

/// <summary>
/// The board's last known state. While disconnected the last values are kept with <see cref="IsConnected"/> false.
/// </summary>
/// <param name="IsConnected">The board answered a poll on the current connection.</param>
/// <param name="Status">The last answer, or null before the first.</param>
/// <param name="LastContactUtc">When the board last answered a poll, or null if it never has.</param>
public sealed record Vm208State(bool IsConnected, Vm208Status? Status, DateTimeOffset? LastContactUtc)
{
    public static Vm208State Initial { get; } = new(false, null, null);

    public byte Relays => Status?.Relays ?? 0;

    public bool Input => Status?.Input ?? false;

    /// <summary>The board's own rules as last read; null while unknown.</summary>
    public OwnRules? OwnRules => Status?.OwnRules;

    public bool IsOn(int relay)
    {
        return relay is >= 1 and <= Vm208Api.RelayCount && (Relays & (1 << (relay - 1))) != 0;
    }
}
