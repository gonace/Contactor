using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Vm204;

/// <summary>
/// The VM204 as an <see cref="IRelayBoard"/>: 4 relays, 4 digital inputs and an analog input, which it never reports
/// by itself, so <see cref="Vm204Client"/> polls it. This only translates the client's state and commands.
/// </summary>
public sealed class Vm204Board : IRelayBoard
{
    public static readonly BoardCapabilities Vm204Capabilities = new(
        Vm204Api.RelayCount,
        InputCount: Vm204Api.InputCount,
        ReportsChanges: false,
        AnalogInputCount: 1);

    private readonly Vm204Client _client;
    private readonly ILogger<Vm204Board> _logger;

    public Vm204Board(Vm204Client client, ILogger<Vm204Board> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
        _client.StateChanged += OnStateChanged;
    }

    public event EventHandler<BoardState>? StateChanged;

    public BoardCapabilities Capabilities => Vm204Capabilities;

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

    // No pulse yet (Issue #83).
    public Task<ChannelMask> PulseAsync(ChannelMask relays, TimeSpan length, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This board can't pulse a relay.");
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _client.Refresh();
        return Task.CompletedTask;
    }

    /// <summary>The VM204's state as a <see cref="BoardState"/>; the analog input isn't part of it.</summary>
    public static BoardState ToBoardState(Vm204State state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new BoardState(state.IsConnected, new ChannelMask(state.Relays), new ChannelMask(state.Inputs), state.LastContactUtc);
    }

    /// <summary>The board's relay mask: relays 1–4 only.</summary>
    public static byte ToMask(ChannelMask relays)
    {
        if (!Vm204Capabilities.AllRelays.ContainsAll(relays))
        {
            throw new ArgumentOutOfRangeException(nameof(relays), relays, "The VM204 has relays 1–4 only.");
        }

        return (byte)relays.Bits;
    }

    // Each subscriber on its own, as for the VM201: one that fails must not keep the state from the others.
    private void OnStateChanged(object? sender, Vm204State state)
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
