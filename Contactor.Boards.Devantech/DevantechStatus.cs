namespace Contactor.Boards.Devantech;

/// <summary>One answer to a poll: which relays and which inputs are on, and what the analog inputs read.</summary>
/// <param name="Relays">Bit per relay, bit 0 = relay 1; 1 = on.</param>
/// <param name="Inputs">Bit per input, bit 0 = input 1; 1 = active. Always 0 without inputs.</param>
/// <param name="Analog">
/// What each analog input read, 0 to 1023, input 1 first; null on a model without analog inputs. Every input is null
/// ("no reading") after an analog answer that failed, and for a minute after it
/// (<see cref="DevantechClient.AnalogRetryInterval"/>).
/// </param>
public sealed record DevantechStatus(ulong Relays, ulong Inputs, AnalogReadings? Analog = null)
{
    /// <summary>
    /// Compares the relays and inputs only: an analog value moves a little on every reading, so it alone is no state
    /// change, which would wake the safety services. It comes as <see cref="DevantechClient.AnalogChanged"/> instead.
    /// </summary>
    public static IEqualityComparer<DevantechStatus> SwitchesComparer { get; } = new Switches();

    private sealed class Switches : IEqualityComparer<DevantechStatus>
    {
        public bool Equals(DevantechStatus? x, DevantechStatus? y)
        {
            return ReferenceEquals(x, y) || (x is not null && y is not null && x.Relays == y.Relays && x.Inputs == y.Inputs);
        }

        public int GetHashCode(DevantechStatus obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            return HashCode.Combine(obj.Relays, obj.Inputs);
        }
    }
}

/// <summary>
/// The board's last known state. While disconnected the last values are kept with <see cref="IsConnected"/> false.
/// </summary>
/// <param name="IsConnected">The board answered a poll on the current connection.</param>
/// <param name="Status">The last answer, or null before the first.</param>
/// <param name="LastContactUtc">When the board last answered a poll, or null if it never has.</param>
/// <param name="Problem">Why the board refused the last connection: another model, or a password needed or refused.</param>
public sealed record DevantechState(bool IsConnected, DevantechStatus? Status, DateTimeOffset? LastContactUtc, ConnectionProblem? Problem = null)
{
    public static DevantechState Initial { get; } = new(false, null, null);

    public ulong Relays => Status?.Relays ?? 0;

    public ulong Inputs => Status?.Inputs ?? 0;

    public bool IsOn(int relay)
    {
        return new ChannelMask(Relays).Contains(relay);
    }
}
