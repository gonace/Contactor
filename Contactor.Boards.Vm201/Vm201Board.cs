using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Vm201;

/// <summary>
/// The VM201 as an <see cref="IRelayBoard"/>: 8 relays and one input, which it reports by itself on every change. The
/// connection itself is <see cref="Vm201Client"/>'s; this only translates its state and commands.
/// </summary>
public sealed class Vm201Board : IRelayBoard
{
    public static readonly BoardCapabilities Vm201Capabilities = new(Vm201Channel.Count, InputCount: 1, ReportsChanges: true);

    private readonly Vm201Client _client;
    private readonly ILogger<Vm201Board> _logger;

    public Vm201Board(Vm201Client client, ILogger<Vm201Board> logger)
    {
        _client = client;
        _logger = logger;
        _client.StateChanged += OnStateChanged;
    }

    public event EventHandler<BoardState>? StateChanged;

    public BoardCapabilities Capabilities => Vm201Capabilities;

    public BoardState State => ToBoardState(_client.State);

    // The board takes the mask at once: all of it, or the command fails.
    public async Task<ChannelMask> TurnOnAsync(ChannelMask relays, CancellationToken cancellationToken = default)
    {
        await _client.TurnOnAsync(ToMask(relays), cancellationToken).ConfigureAwait(false);
        return relays;
    }

    public async Task<ChannelMask> TurnOffAsync(ChannelMask relays, CancellationToken cancellationToken = default)
    {
        await _client.TurnOffAsync(ToMask(relays), cancellationToken).ConfigureAwait(false);
        return relays;
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
        if (!Vm201Capabilities.AllRelays.ContainsAll(relays))
        {
            throw new ArgumentOutOfRangeException(nameof(relays), relays, "The VM201 has relays 1–8 only.");
        }

        return (byte)relays.Bits;
    }

    // Each subscriber on its own, as the client does: one that fails must not keep the state from the others, e.g.
    // the group sequencer, which checks the interlock on every state.
    private void OnStateChanged(object? sender, Vm201State state)
    {
        if (StateChanged is not { } handlers)
        {
            return;
        }

        var boardState = ToBoardState(state);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<BoardState>>())
        {
            try
            {
                handler(this, boardState);
            }
#pragma warning disable CA1031 // A subscriber's bug must not take down the connection or the other subscribers.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.SubscriberFailed(ex);
            }
        }
    }
}
