using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Vm208;

/// <summary>
/// The VM208 as an <see cref="IRelayBoard"/>: 4 relays and 1 digital input, which it never reports by itself, so
/// <see cref="Vm208Client"/> polls it. Its two transistor outputs (m1, m2) are not relays and aren't counted. This only
/// translates the client's state and commands.
/// </summary>
public sealed class Vm208Board : IRelayBoard
{
    public static readonly BoardCapabilities Vm208Capabilities = new(
        Vm208Api.RelayCount,
        InputCount: Vm208Api.InputCount,
        ReportsChanges: false);

    private readonly Vm208Client _client;
    private readonly ILogger<Vm208Board> _logger;

    public Vm208Board(Vm208Client client, ILogger<Vm208Board> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
        _client.StateChanged += OnStateChanged;
    }

    public event EventHandler<BoardState>? StateChanged;

    public BoardCapabilities Capabilities => Vm208Capabilities;

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

    /// <summary>The VM208's state as a <see cref="BoardState"/>; the transistor outputs aren't part of it.</summary>
    public static BoardState ToBoardState(Vm208State state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new BoardState(state.IsConnected, new ChannelMask(state.Relays), state.Input ? ChannelMask.Of(1) : ChannelMask.None, state.LastContactUtc);
    }

    /// <summary>The board's relay mask: relays 1–4 only.</summary>
    public static byte ToMask(ChannelMask relays)
    {
        if (!Vm208Capabilities.AllRelays.ContainsAll(relays))
        {
            throw new ArgumentOutOfRangeException(nameof(relays), relays, "The VM208 has relays 1–4 only.");
        }

        return (byte)relays.Bits;
    }

    // Each subscriber on its own, as for the VM201: one that fails must not keep the state from the others.
    private void OnStateChanged(object? sender, Vm208State state)
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
