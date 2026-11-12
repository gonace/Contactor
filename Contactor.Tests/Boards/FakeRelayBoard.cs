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

    public FakeRelayBoard(int relayCount = 8, bool connected = true, int inputCount = 1)
    {
        Capabilities = new BoardCapabilities(relayCount, inputCount, ReportsChanges: true);
        _state = new BoardState(connected, ChannelMask.None, ChannelMask.None, connected ? DateTimeOffset.UnixEpoch : null);
    }

    public event EventHandler<BoardState>? StateChanged;

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

    public Task TurnOnAsync(ChannelMask relays, CancellationToken cancellationToken = default)
    {
        return SwitchAsync(relays, on: true);
    }

    /// <summary>When set, OFF commands are accepted but change nothing, like a relay that won't drop.</summary>
    public bool IgnoreOff { get; set; }

    /// <summary>When set, the next OFF throws <see cref="BoardNotConnectedException"/>, as if the connection had just dropped.</summary>
    public bool FailNextOff { get; set; }

    public Task TurnOffAsync(ChannelMask relays, CancellationToken cancellationToken = default)
    {
        if (FailNextOff)
        {
            FailNextOff = false;
            throw new BoardNotConnectedException();
        }

        if (IgnoreOff)
        {
            EnsureConnected();
            return Task.CompletedTask;
        }

        return SwitchAsync(relays, on: false);
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
            state = _state = _state with
            {
                Relays = on ? _state.Relays | ChannelMask.Of(relay) : _state.Relays.Except(ChannelMask.Of(relay)),
                Inputs = on ? _state.Inputs | ChannelMask.Of(input) : _state.Inputs.Except(ChannelMask.Of(input)),
            };
        }

        StateChanged?.Invoke(this, state);
    }

    /// <summary>An input changes at the board, and the board reports it.</summary>
    public void SetInput(int input, bool on)
    {
        BoardState state;
        lock (_lock)
        {
            state = _state = _state with { Inputs = on ? _state.Inputs | ChannelMask.Of(input) : _state.Inputs.Except(ChannelMask.Of(input)) };
        }

        StateChanged?.Invoke(this, state);
    }

    /// <summary>The connection opens or ends, and the board reports it, as a real board's client does.</summary>
    public void SetConnected(bool connected)
    {
        BoardState state;
        lock (_lock)
        {
            state = _state = _state with { IsConnected = connected };
        }

        StateChanged?.Invoke(this, state);
    }

    private Task SwitchAsync(ChannelMask relays, bool on)
    {
        EnsureConnected();
        if (!Capabilities.AllRelays.ContainsAll(relays))
        {
            throw new ArgumentOutOfRangeException(nameof(relays), relays, $"The board has relays 1–{Capabilities.RelayCount}.");
        }

        BoardState state;
        lock (_lock)
        {
            state = _state = _state with { Relays = on ? _state.Relays | relays : _state.Relays.Except(relays) };
        }

        StateChanged?.Invoke(this, state);
        return Task.CompletedTask;
    }

    private void EnsureConnected()
    {
        if (!State.IsConnected)
        {
            throw new BoardNotConnectedException();
        }
    }
}
