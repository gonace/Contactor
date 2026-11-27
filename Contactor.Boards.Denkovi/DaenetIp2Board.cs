using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Denkovi;

/// <summary>
/// A Denkovi DAEnetIP2 v2 kit as an <see cref="IRelayBoard"/>: its kit's relays and the analog inputs of P6, which it
/// never reports by itself, so <see cref="DaenetIp2Client"/> polls it. It has no pulse: the manual documents none per
/// relay over SNMP. This only translates the client's state and commands. A change in the analog values alone comes as
/// <see cref="AnalogChanged"/>, one in the board's own rules (the ICMP monitoring's restart pins) alone as
/// <see cref="OwnRulesChanged"/>.
/// </summary>
public sealed class DaenetIp2Board : IRelayBoard
{
    private readonly DaenetIp2Client _client;
    private readonly ILogger<DaenetIp2Board> _logger;

    public DaenetIp2Board(DaenetIp2Client client, ILogger<DaenetIp2Board> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
        _client.StateChanged += OnStateChanged;
        _client.AnalogChanged += OnAnalogChanged;
        _client.OwnRulesChanged += OnOwnRulesChanged;
    }

    public event EventHandler<BoardState>? StateChanged;

    public event EventHandler<BoardState>? AnalogChanged;

    public event EventHandler<BoardState>? OwnRulesChanged;

    public BoardCapabilities Capabilities => _client.Model.Capabilities;

    public BoardState State => ToBoardState(_client.Model, _client.State);

    // A relay per request: only the relays the board took.
    public async Task<ChannelMask> TurnOnAsync(ChannelMask relays, CancellationToken cancellationToken = default)
    {
        return new ChannelMask(await _client.TurnOnAsync(ToMask(relays), cancellationToken).ConfigureAwait(false));
    }

    public async Task<ChannelMask> TurnOffAsync(ChannelMask relays, CancellationToken cancellationToken = default)
    {
        return new ChannelMask(await _client.TurnOffAsync(ToMask(relays), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Throws <see cref="NotSupportedException"/>: the board has no pulse (<see cref="BoardCapabilities.Pulse"/> is null).</summary>
    public Task<ChannelMask> PulseAsync(ChannelMask relays, TimeSpan length, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException($"The {_client.Model.Name} has no pulse.");
    }

    /// <summary>
    /// Turns off the ICMP monitoring's restart pins of the <see cref="OwnRuleKind.Restart"/> rules' relays, with one write
    /// of <c>cfgP5DupRST</c> between a read and a read-back (<see cref="DaenetIp2Client.TurnOffOwnRulesAsync"/>).
    /// </summary>
    public Task<OwnRulesTurnOff> TurnOffOwnRulesAsync(IReadOnlyCollection<OwnRule> rules, CancellationToken cancellationToken = default)
    {
        return _client.TurnOffOwnRulesAsync(rules, cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _client.Refresh();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The board's state as a <see cref="BoardState"/> for the kit <paramref name="model"/>. Its analog readings are null
    /// before the first poll; the client reads only values the board can give, 0 to 1023, and null for each input when an
    /// analog answer failed. Its own rules are a <see cref="OwnRuleKind.Restart"/> rule on the relay of each restart pin
    /// (<see cref="DaenetIp2Layout.RestartRules"/>): null while the pins aren't known (before they were read, a firmware
    /// older than 4.097, or a read that failed), and kept while disconnected, as the relays are.
    /// </summary>
    public static BoardState ToBoardState(BoardModel model, DaenetIp2State state)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(state);
        return new BoardState(
            state.IsConnected,
            new ChannelMask(state.Relays),
            ChannelMask.None,
            state.LastContactUtc,
            Analog: state.Status?.Analog,
            OwnRules: state.Status?.RestartPins is { } pins ? DaenetIp2Models.Layout(model).RestartRules(pins) : null);
    }

    // Only relays the kit has; throws before anything is sent.
    private ulong ToMask(ChannelMask relays)
    {
        if (!Capabilities.AllRelays.ContainsAll(relays))
        {
            throw new ArgumentOutOfRangeException(nameof(relays), relays, $"The {_client.Model.Name} has relays 1–{Capabilities.RelayCount} only.");
        }

        return relays.Bits;
    }

    private void OnStateChanged(object? sender, DaenetIp2State state)
    {
        Raise(StateChanged, state);
    }

    private void OnAnalogChanged(object? sender, DaenetIp2State state)
    {
        Raise(AnalogChanged, state);
    }

    // Only the restart pins changed: not a change the safety services, which listen to StateChanged, must see.
    private void OnOwnRulesChanged(object? sender, DaenetIp2State state)
    {
        Raise(OwnRulesChanged, state);
    }

    // Each subscriber on its own, as for the other boards: one that fails must not keep the state from the others.
    private void Raise(EventHandler<BoardState>? handlers, DaenetIp2State state)
    {
        if (handlers is null)
        {
            return;
        }

        var boardState = ToBoardState(_client.Model, state);
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
                DaenetIp2Log.SubscriberFailed(_logger, ex);
            }
        }
    }
}
