using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Waveshare;

/// <summary>
/// A Waveshare Modbus PoE ETH relay as an <see cref="IRelayBoard"/>: its model's relays and inputs, which it never
/// reports by itself, so <see cref="WaveshareClient"/> polls it. This only translates the client's state and commands.
/// </summary>
public sealed class WaveshareBoard : IRelayBoard
{
    private readonly WaveshareClient _client;
    private readonly ILogger<WaveshareBoard> _logger;

    public WaveshareBoard(WaveshareClient client, ILogger<WaveshareBoard> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
        _client.StateChanged += OnStateChanged;
    }

    public event EventHandler<BoardState>? StateChanged;

    public BoardCapabilities Capabilities => _client.Model.Capabilities;

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
        _client.Refresh();
        return Task.CompletedTask;
    }

    /// <summary>The board's state as a <see cref="BoardState"/>.</summary>
    public static BoardState ToBoardState(WaveshareState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new BoardState(state.IsConnected, new ChannelMask(state.Relays), new ChannelMask(state.Inputs), state.LastContactUtc);
    }

    // Only relays the model has; throws before anything is sent.
    private ulong ToMask(ChannelMask relays)
    {
        if (!Capabilities.AllRelays.ContainsAll(relays))
        {
            throw new ArgumentOutOfRangeException(nameof(relays), relays, $"The {_client.Model.Name} has relays 1–{Capabilities.RelayCount} only.");
        }

        return relays.Bits;
    }

    // Each subscriber on its own, as for the other boards: one that fails must not keep the state from the others.
    private void OnStateChanged(object? sender, WaveshareState state)
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
#pragma warning disable CA1031 // A subscriber's bug must not take down the poller or the other subscribers.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.SubscriberFailed(ex);
            }
        }
    }
}
