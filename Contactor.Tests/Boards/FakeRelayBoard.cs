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

    public FakeRelayBoard(int relayCount = 8, bool connected = true)
    {
        Capabilities = new BoardCapabilities(relayCount, InputCount: 1, ReportsChanges: true);
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

    public Task TurnOffAsync(ChannelMask relays, CancellationToken cancellationToken = default)
    {
        return SwitchAsync(relays, on: false);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        StateChanged?.Invoke(this, State);
        return Task.CompletedTask;
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
