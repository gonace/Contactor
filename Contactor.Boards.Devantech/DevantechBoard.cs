using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Devantech;

/// <summary>
/// A Devantech ETH relay board as an <see cref="IRelayBoard"/>: its model's relays, inputs and analog inputs, which it
/// never reports by itself, so <see cref="DevantechClient"/> polls it. This only translates the client's state and
/// commands. A change in the analog values alone comes as <see cref="AnalogChanged"/>.
/// </summary>
public sealed class DevantechBoard : IRelayBoard
{
    private readonly DevantechClient _client;
    private readonly ILogger<DevantechBoard> _logger;

    public DevantechBoard(DevantechClient client, ILogger<DevantechBoard> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
        _client.StateChanged += OnStateChanged;
        _client.AnalogChanged += OnAnalogChanged;
    }

    public event EventHandler<BoardState>? StateChanged;

    public event EventHandler<BoardState>? AnalogChanged;

    public BoardCapabilities Capabilities => _client.Model.Capabilities;

    public BoardState State => ToBoardState(_client.State);

    // A relay per request: only the relays the board took.
    public async Task<ChannelMask> TurnOnAsync(ChannelMask relays, CancellationToken cancellationToken = default)
    {
        return new ChannelMask(await _client.TurnOnAsync(ToMask(relays), cancellationToken).ConfigureAwait(false));
    }

    public async Task<ChannelMask> TurnOffAsync(ChannelMask relays, CancellationToken cancellationToken = default)
    {
        return new ChannelMask(await _client.TurnOffAsync(ToMask(relays), cancellationToken).ConfigureAwait(false));
    }

    // A relay per request, as ON: only the relays the board took. The length is checked before the relays, so
    // anything wrong throws before a request goes out.
    public async Task<ChannelMask> PulseAsync(ChannelMask relays, TimeSpan length, CancellationToken cancellationToken = default)
    {
        if (!DevantechModels.Pulse.CanDo(length))
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "A Devantech board pulses for 0.1 s to 25.5 s, in steps of 0.1 s.");
        }

        return new ChannelMask(await _client.PulseAsync(ToMask(relays), length, cancellationToken).ConfigureAwait(false));
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _client.Refresh();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The board's state as a <see cref="BoardState"/>. Its analog readings are null on a model without analog inputs
    /// and before the first answer; the client reads only values the board can give, 0 to 1023.
    /// </summary>
    public static BoardState ToBoardState(DevantechState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new BoardState(state.IsConnected, new ChannelMask(state.Relays), new ChannelMask(state.Inputs), state.LastContactUtc, state.Problem, state.Status?.Analog);
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

    private void OnStateChanged(object? sender, DevantechState state)
    {
        Raise(StateChanged, state);
    }

    private void OnAnalogChanged(object? sender, DevantechState state)
    {
        Raise(AnalogChanged, state);
    }

    // Each subscriber on its own, as for the other boards: one that fails must not keep the state from the others.
    private void Raise(EventHandler<BoardState>? handlers, DevantechState state)
    {
        if (handlers is null)
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
