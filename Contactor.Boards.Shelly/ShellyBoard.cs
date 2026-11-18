using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Shelly;

/// <summary>
/// A Shelly Gen2+ device as an <see cref="IRelayBoard"/>: its entry's 1 to 4 relays, which it never reports by itself
/// over HTTP, so <see cref="ShellyClient"/> polls it, and each relay's state after a power cut (its switch's
/// <c>initial_state</c>), which the app sets only when a person saves it. This only translates the client's state and
/// commands.
/// </summary>
public sealed class ShellyBoard : IRelayBoard
{
    private readonly ShellyClient _client;
    private readonly ILogger<ShellyBoard> _logger;

    public ShellyBoard(ShellyClient client, ILogger<ShellyBoard> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
        _client.StateChanged += OnStateChanged;
        _client.PowerOnChanged += OnPowerOnChanged;
    }

    public event EventHandler<BoardState>? StateChanged;

    /// <inheritdoc/>
    public event EventHandler<BoardState>? PowerOnChanged;

    public BoardCapabilities Capabilities => _client.Model.Capabilities;

    public BoardState State => ToBoardState(_client.State);

    // One Switch.Set per relay: only the relays the device took.
    public async Task<ChannelMask> TurnOnAsync(ChannelMask relays, CancellationToken cancellationToken = default)
    {
        return new ChannelMask(await _client.TurnOnAsync(ToMask(relays), cancellationToken).ConfigureAwait(false));
    }

    public async Task<ChannelMask> TurnOffAsync(ChannelMask relays, CancellationToken cancellationToken = default)
    {
        return new ChannelMask(await _client.TurnOffAsync(ToMask(relays), cancellationToken).ConfigureAwait(false));
    }

    // Switch.Set's toggle_after isn't used yet.
    public Task<ChannelMask> PulseAsync(ChannelMask relays, TimeSpan length, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This board can't pulse a relay.");
    }

    /// <summary>One <c>Switch.SetConfig</c> for this relay's switch only, read back at once (<see cref="ShellyClient.SetPowerOnAsync"/>).</summary>
    public Task SetPowerOnAsync(int relay, PowerOnState state, CancellationToken cancellationToken = default)
    {
        return _client.SetPowerOnAsync(relay, state, cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _client.Refresh();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The device's state as a <see cref="BoardState"/>: its relays and their states after a power cut, no inputs or
    /// analog readings.
    /// </summary>
    public static BoardState ToBoardState(ShellyState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new BoardState(state.IsConnected, new ChannelMask(state.Relays), ChannelMask.None, state.LastContactUtc, state.Problem, PowerOn: state.PowerOn);
    }

    // Only relays the entry has; throws before anything is sent.
    private byte ToMask(ChannelMask relays)
    {
        if (!Capabilities.AllRelays.ContainsAll(relays))
        {
            throw new ArgumentOutOfRangeException(nameof(relays), relays, $"The {_client.Model.Name} has relays 1–{Capabilities.RelayCount} only.");
        }

        return (byte)relays.Bits;
    }

    private void OnStateChanged(object? sender, ShellyState state)
    {
        Raise(StateChanged, state);
    }

    // Only the power-on states changed: not a change the safety services, which listen to StateChanged, must see.
    private void OnPowerOnChanged(object? sender, ShellyState state)
    {
        Raise(PowerOnChanged, state);
    }

    // Each subscriber on its own, as for the other boards: one that fails must not keep the state from the others.
    private void Raise(EventHandler<BoardState>? handlers, ShellyState state)
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
