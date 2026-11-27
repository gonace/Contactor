using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Shelly;

/// <summary>
/// A Shelly Gen2+ device as an <see cref="IRelayBoard"/>: its entry's 1 to 4 relays, which it never reports by itself
/// over HTTP, so <see cref="ShellyClient"/> polls it, each relay's state after a power cut (its switch's
/// <c>initial_state</c>), which the app sets only when a person saves it, and the device's own schedules, auto-on and
/// inputs that switch a relay, which the app turns off only on a person's press, and the inputs the device has, as it gave them at the connection
/// (<see cref="BoardCapabilities.InputCountFromBoard"/>). This only translates the client's state and commands.
/// </summary>
public sealed class ShellyBoard : IRelayBoard
{
    private readonly ShellyClient _client;
    private readonly ILogger<ShellyBoard> _logger;

    // The entry's capabilities with each input count the device may give, 0 to the most: no new one on every call.
    private readonly BoardCapabilities[] _byInputCount;

    public ShellyBoard(ShellyClient client, ILogger<ShellyBoard> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
        _byInputCount = [.. Enumerable.Range(0, client.Model.Capabilities.InputCount + 1).Select(count => client.Model.Capabilities with { InputCount = count })];
        _client.StateChanged += OnStateChanged;
        _client.PowerOnChanged += OnPowerOnChanged;
        _client.OwnRulesChanged += OnOwnRulesChanged;
    }

    public event EventHandler<BoardState>? StateChanged;

    /// <inheritdoc/>
    public event EventHandler<BoardState>? PowerOnChanged;

    /// <inheritdoc/>
    public event EventHandler<BoardState>? OwnRulesChanged;

    /// <summary>
    /// The entry's capabilities, with as many inputs as the device gave on its last connection: none before it answered
    /// (<see cref="BoardCapabilities.InputCountFromBoard"/>).
    /// </summary>
    public BoardCapabilities Capabilities => _byInputCount[Math.Clamp(_client.State.InputCount, 0, _byInputCount.Length - 1)];

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

    /// <summary>
    /// Turns off the device's schedules behind the <see cref="OwnRuleKind.Timer"/> rules, the auto-on of the
    /// <see cref="OwnRuleKind.AutoOn"/> rules and the input mode of the <see cref="OwnRuleKind.InputEdge"/> and
    /// <see cref="OwnRuleKind.InputToggle"/> rules (<see cref="ShellyClient.TurnOffOwnRulesAsync"/>), and reads them back.
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
    /// The device's state as a <see cref="BoardState"/>: its relays, their states after a power cut and its own rules
    /// (null while unknown), and its inputs; no analog readings.
    /// </summary>
    public static BoardState ToBoardState(ShellyState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new BoardState(state.IsConnected, new ChannelMask(state.Relays), new ChannelMask(state.Inputs), state.LastContactUtc, state.Problem, OwnRules: state.OwnRules, PowerOn: state.PowerOn, UnknownInputs: new ChannelMask(state.UnknownInputs), NotSwitchInputs: new ChannelMask(state.NotSwitchInputs));
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

    // Only the own rules changed: not a change the safety services, which listen to StateChanged, must see.
    private void OnOwnRulesChanged(object? sender, ShellyState state)
    {
        Raise(OwnRulesChanged, state);
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
