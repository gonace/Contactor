using Contactor.Boards;

namespace Contactor.Tests.Boards;

/// <summary>
/// A relay board in memory, with any number of relays: commands switch its relays at once and report the new state,
/// as a board that reports its changes does. For tests that need no real board, or a board with other than 8 relays.
/// </summary>
internal sealed class FakeRelayBoard : IRelayBoard
{
    private readonly Lock _lock = new();
    private BoardState _state;

    public FakeRelayBoard(int relayCount = 8, bool connected = true, int inputCount = 1, PulseAbility? pulse = null)
    {
        Capabilities = new BoardCapabilities(relayCount, inputCount, ReportsChanges: true, Pulse: pulse);
        _state = new BoardState(connected, ChannelMask.None, ChannelMask.None, connected ? DateTimeOffset.UnixEpoch : null);
    }

    /// <summary>
    /// A board that answered once and then lost contact: its last known state is shown with a warning, unlike a board
    /// that never answered, whose states are unknown.
    /// </summary>
    public static FakeRelayBoard LostContact(int relayCount = 8, int inputCount = 1)
    {
        var board = new FakeRelayBoard(relayCount, connected: true, inputCount);
        board.SetConnected(false);
        return board;
    }

    public event EventHandler<BoardState>? StateChanged;

    /// <summary>Whether anything listens to its state, e.g. to tell a subscription that was let go.</summary>
    public bool HasListeners => StateChanged is not null;

    /// <summary>
    /// When set, every status the board reports while connected carries this clock's time as its contact time, as a
    /// real board's does; otherwise it keeps the time it started with.
    /// </summary>
    public TimeProvider? Time { get; set; }

    public BoardCapabilities Capabilities { get; }

    public BoardState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    public Task<ChannelMask> TurnOnAsync(ChannelMask relays, CancellationToken cancellationToken = default)
    {
        return SwitchAsync(relays, on: true);
    }

    /// <summary>
    /// Relays the board refuses, as one that switches a relay per request can: they stay as they are and aren't among
    /// the relays a command returns. A command that has only these fails, as on a real board.
    /// </summary>
    public ChannelMask Refuse { get; set; }

    /// <summary>
    /// Relays the board switches and reports, but leaves out of the relays a command returns, as one whose answer for
    /// them couldn't be read although they did switch. A command that has only these switches them, reports them and
    /// then fails, as a real board's client does: the read after it shows what switched.
    /// </summary>
    public ChannelMask Unconfirmed { get; set; }

    /// <summary>
    /// When set, the next command switches the relays but reports nothing and throws
    /// <see cref="BoardNotConnectedException"/>, as a board whose answer was lost or unreadable after it switched. The
    /// change shows on the next <see cref="RefreshAsync"/>.
    /// </summary>
    public bool SwitchSilentlyThenFail { get; set; }

    /// <summary>
    /// When set, commands switch the relays but the board reports nothing until <see cref="ReleaseStatus"/>, like a
    /// board whose status is still on its way: <see cref="State"/> keeps showing the relays as they were.
    /// </summary>
    public bool HoldStatus { get; set; }

    private ChannelMask? _heldRelays;

    /// <summary>Reports the relays the held commands switched, and stops holding.</summary>
    public void ReleaseStatus()
    {
        BoardState state;
        lock (_lock)
        {
            HoldStatus = false;
            state = _state = _state with { Relays = _heldRelays ?? _state.Relays };
            _heldRelays = null;
        }

        StateChanged?.Invoke(this, state);
    }

    /// <summary>
    /// Runs after a command switched the relays and the board reported it, before the command returns: something else
    /// the app does while the caller hasn't saved what it did yet.
    /// </summary>
    public Func<Task>? AfterSwitch { get; set; }

    /// <summary>
    /// Runs before an ON reaches the board, e.g. to hold it on its way (a slow request) while something else happens.
    /// </summary>
    public Func<Task>? BeforeOn { get; set; }

    /// <summary>
    /// When set, OFF commands are accepted but change nothing, like a relay that won't drop. They return the relays as
    /// any command does: without <see cref="Refuse"/> and <see cref="Unconfirmed"/>.
    /// </summary>
    public bool IgnoreOff { get; set; }

    /// <summary>When set, the next OFF throws <see cref="BoardNotConnectedException"/>, as if the connection had just dropped.</summary>
    public bool FailNextOff { get; set; }

    public Task<ChannelMask> TurnOffAsync(ChannelMask relays, CancellationToken cancellationToken = default)
    {
        if (FailNextOff)
        {
            FailNextOff = false;
            throw new BoardNotConnectedException();
        }

        return SwitchAsync(relays, on: false);
    }

    /// <summary>The pulses sent: the relays and how long, in order.</summary>
    public IReadOnlyList<(ChannelMask Relays, TimeSpan Length)> Pulses
    {
        get
        {
            lock (_lock)
            {
                return [.. _pulses];
            }
        }
    }

    private readonly List<(ChannelMask Relays, TimeSpan Length)> _pulses = [];

    /// <summary>
    /// Turns the relays on, as a board's pulse does; <see cref="EndPulsesAsync"/> turns them off again, as the board does
    /// by itself once the time is up. Only on a board made with a pulse.
    /// </summary>
    public async Task<ChannelMask> PulseAsync(ChannelMask relays, TimeSpan length, CancellationToken cancellationToken = default)
    {
        if (Capabilities.Pulse is not { } pulse)
        {
            throw new NotSupportedException("This board can't pulse a relay.");
        }

        if (!pulse.CanDo(length))
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The board can't pulse for that long.");
        }

        var taken = await SwitchAsync(relays, on: true);
        lock (_lock)
        {
            _pulses.Add((taken, length));
        }

        return taken;
    }

    /// <summary>The board's own end of every pulse sent: their relays go off, and the board reports it.</summary>
    public async Task EndPulsesAsync()
    {
        ChannelMask relays;
        lock (_lock)
        {
            relays = _pulses.Aggregate(ChannelMask.None, (all, p) => all | p.Relays);
        }

        await SwitchAsync(relays, on: false);
    }

    /// <summary>When set, status requests go out but the board never answers them.</summary>
    public bool IgnoreRefresh { get; set; }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (IgnoreRefresh)
        {
            return Task.CompletedTask;
        }

        StateChanged?.Invoke(this, State);
        return Task.CompletedTask;
    }

    /// <summary>A relay and an input change together, and the board reports both in one status.</summary>
    public void SetRelayAndInput(int relay, int input, bool on)
    {
        BoardState state;
        lock (_lock)
        {
            state = _state = Stamp(_state with
            {
                Relays = on ? _state.Relays | ChannelMask.Of(relay) : _state.Relays.Except(ChannelMask.Of(relay)),
                Inputs = on ? _state.Inputs | ChannelMask.Of(input) : _state.Inputs.Except(ChannelMask.Of(input)),
            });
        }

        StateChanged?.Invoke(this, state);
    }

    /// <summary>A relay switches at the board, e.g. by its own button or a command that arrived late, and the board reports it.</summary>
    public void SetRelay(int relay, bool on)
    {
        BoardState state;
        lock (_lock)
        {
            state = _state = Stamp(_state with { Relays = on ? _state.Relays | ChannelMask.Of(relay) : _state.Relays.Except(ChannelMask.Of(relay)) });
        }

        StateChanged?.Invoke(this, state);
    }

    /// <summary>An input changes at the board, and the board reports it.</summary>
    public void SetInput(int input, bool on)
    {
        BoardState state;
        lock (_lock)
        {
            state = _state = Stamp(_state with { Inputs = on ? _state.Inputs | ChannelMask.Of(input) : _state.Inputs.Except(ChannelMask.Of(input)) });
        }

        StateChanged?.Invoke(this, state);
    }

    /// <summary>The board's state is forgotten, as a client does when its address changes: nothing is known.</summary>
    public void Forget()
    {
        BoardState state;
        lock (_lock)
        {
            state = _state = new BoardState(false, ChannelMask.None, ChannelMask.None, null);
        }

        StateChanged?.Invoke(this, state);
    }

    /// <summary>The connection opens or ends, and the board reports it, as a real board's client does.</summary>
    public void SetConnected(bool connected)
    {
        BoardState state;
        lock (_lock)
        {
            state = _state = Stamp(_state with { IsConnected = connected });
        }

        StateChanged?.Invoke(this, state);
    }

    /// <summary>Every command that reached the board while it was connected: its relays and whether ON, in order.</summary>
    public IReadOnlyList<(ChannelMask Relays, bool On)> Sent
    {
        get
        {
            lock (_lock)
            {
                return [.. _sent];
            }
        }
    }

    private readonly List<(ChannelMask Relays, bool On)> _sent = [];

    private async Task<ChannelMask> SwitchAsync(ChannelMask relays, bool on)
    {
        if (on && BeforeOn is { } before)
        {
            await before();
        }

        EnsureConnected();
        if (!Capabilities.AllRelays.ContainsAll(relays))
        {
            throw new ArgumentOutOfRangeException(nameof(relays), relays, $"The board has relays 1–{Capabilities.RelayCount}.");
        }

        lock (_lock)
        {
            _sent.Add((relays, on));
        }

        // Refused relays stay as they are: a command with only these fails before it switches anything.
        var taken = relays.Except(Refuse);
        if (!relays.IsEmpty && taken.IsEmpty)
        {
            throw new BoardNotConnectedException("The board refused the command.");
        }

        if (!on && IgnoreOff)
        {
            return Confirmed(relays, taken);
        }

        BoardState state;
        lock (_lock)
        {
            if (HoldStatus)
            {
                var held = _heldRelays ?? _state.Relays;
                _heldRelays = on ? held | taken : held.Except(taken);
                return Confirmed(relays, taken);
            }

            state = _state = Stamp(_state with { Relays = on ? _state.Relays | taken : _state.Relays.Except(taken) });
        }

        if (SwitchSilentlyThenFail)
        {
            SwitchSilentlyThenFail = false;
            throw new BoardNotConnectedException("The board's answer couldn't be read.");
        }

        StateChanged?.Invoke(this, state);
        if (AfterSwitch is { } after)
        {
            await after();
        }

        return Confirmed(relays, taken);
    }

    // The relays a command returns: those it took, less the unconfirmed. A command that switched only unconfirmed
    // relays fails, as on a real board, whose client can't tell that anything switched.
    private ChannelMask Confirmed(ChannelMask relays, ChannelMask taken)
    {
        var confirmed = taken.Except(Unconfirmed);
        if (!relays.IsEmpty && confirmed.IsEmpty)
        {
            throw new BoardNotConnectedException("The board's answers couldn't be read.");
        }

        return confirmed;
    }

    private void EnsureConnected()
    {
        if (!State.IsConnected)
        {
            throw new BoardNotConnectedException();
        }
    }

    private BoardState Stamp(BoardState state)
    {
        return Time is { } time && state.IsConnected ? state with { LastContactUtc = time.GetUtcNow() } : state;
    }
}
