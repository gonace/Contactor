namespace Contactor.Boards.Vm204;

/// <summary>One answer to a status request: which relays and inputs are on, and the analog input.</summary>
/// <param name="Relays">Bit per relay, bit 0 = relay 1; 1 = on.</param>
/// <param name="Inputs">Bit per digital input, bit 0 = input 1; 1 = on (<c>true</c> in the board's JSON).</param>
/// <param name="Analog">The analog input as the board's 10-bit converter reads it, or null when the answer had none.</param>
public sealed record Vm204Status(byte Relays, byte Inputs, int? Analog)
{
    /// <summary>
    /// Compares the relays and inputs only: the analog value moves a little on every reading, and the app doesn't show
    /// it, so it alone is no change worth telling every screen about.
    /// </summary>
    public static IEqualityComparer<Vm204Status> SwitchesComparer { get; } = new Switches();

    /// <summary>Whether the relays and inputs are the same as in <paramref name="other"/>.</summary>
    public bool SameSwitches(Vm204Status? other)
    {
        return other is not null && Relays == other.Relays && Inputs == other.Inputs;
    }

    private sealed class Switches : IEqualityComparer<Vm204Status>
    {
        public bool Equals(Vm204Status? x, Vm204Status? y)
        {
            return ReferenceEquals(x, y) || (x is not null && x.SameSwitches(y));
        }

        public int GetHashCode(Vm204Status obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            return HashCode.Combine(obj.Relays, obj.Inputs);
        }
    }
}

/// <summary>
/// The board's last known state. While disconnected the last values are kept with <see cref="IsConnected"/> false.
/// </summary>
/// <param name="IsConnected">Logged in, and the board answered a poll on the current connection.</param>
/// <param name="Status">The last answer, or null before the first.</param>
/// <param name="LastContactUtc">When the board last answered a poll, or null if it never has.</param>
public sealed record Vm204State(bool IsConnected, Vm204Status? Status, DateTimeOffset? LastContactUtc)
{
    public static Vm204State Initial { get; } = new(false, null, null);

    public byte Relays => Status?.Relays ?? 0;

    public byte Inputs => Status?.Inputs ?? 0;

    public bool IsOn(int relay)
    {
        return relay is >= 1 and <= Vm204Api.RelayCount && (Relays & (1 << (relay - 1))) != 0;
    }
}
