
namespace Contactor.Boards.Vm201;

/// <summary>
/// The VM201 as an <see cref="IRelayBoard"/>: 8 relays and one input, which it reports by itself on every change. The
/// connection itself is <see cref="Vm201Client"/>'s; this only translates its state and commands.
/// </summary>
public sealed class Vm201Board : IRelayBoard
{
    public static readonly BoardCapabilities Vm201 = new(Vm201Channel.Count, InputCount: 1, ReportsChanges: true);

    private readonly Vm201Client _client;

    public Vm201Board(Vm201Client client)
    {
        _client = client;
        _client.StateChanged += OnStateChanged;
    }

    public event EventHandler<BoardState>? StateChanged;

    public BoardCapabilities Capabilities => Vm201;

    public BoardState State => ToBoardState(_client.State);

    public Task TurnOnAsync(ChannelMask relays, CancellationToken cancellationToken = default)
    {
        return _client.TurnOnAsync(ToMask(relays), cancellationToken);
    }

    public Task TurnOffAsync(ChannelMask relays, CancellationToken cancellationToken = default)
    {
        return _client.TurnOffAsync(ToMask(relays), cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        return _client.RequestStatusAsync(cancellationToken);
    }

    /// <summary>The VM201's own status as a <see cref="BoardState"/>; its one input is input 1.</summary>
    public static BoardState ToBoardState(Vm201State state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new BoardState(
            state.IsConnected,
            new ChannelMask(state.Outputs),
            state.Input ? ChannelMask.Of(1) : ChannelMask.None,
            state.LastContactUtc);
    }

    /// <summary>The board's channel mask: relays 1–8 only.</summary>
    public static byte ToMask(ChannelMask relays)
    {
        if (!Vm201.AllRelays.ContainsAll(relays))
        {
            throw new ArgumentOutOfRangeException(nameof(relays), relays, "The VM201 has relays 1–8 only.");
        }

        return (byte)relays.Bits;
    }

    private void OnStateChanged(object? sender, Vm201State state)
    {
        StateChanged?.Invoke(this, ToBoardState(state));
    }
}
