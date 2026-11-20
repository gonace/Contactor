using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Waveshare;

/// <summary>
/// A Waveshare Modbus PoE ETH relay as an <see cref="IRelayBoard"/>: its model's relays and inputs, which it never
/// reports by itself, so <see cref="WaveshareClient"/> polls it. On the (B) and (C) its own rules are the relays'
/// control modes (<see cref="ToOwnRules"/>), which the app reads and sets back to normal only on a person's press. This
/// only translates the client's state and commands.
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
        _client.OwnRulesChanged += OnOwnRulesChanged;
    }

    public event EventHandler<BoardState>? StateChanged;

    /// <inheritdoc/>
    public event EventHandler<BoardState>? OwnRulesChanged;

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
        if (!WaveshareModels.Pulse.CanDo(length))
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "A Waveshare board pulses for 0.1 s to 3276.7 s, in steps of 0.1 s.");
        }

        return new ChannelMask(await _client.PulseAsync(ToMask(relays), length, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Sets the relays of <paramref name="rules"/> back to normal mode, one FC 06 each (<see cref="WaveshareClient.TurnOffModesAsync"/>).
    /// Only <see cref="OwnRuleKind.InputLinkage"/>, <see cref="OwnRuleKind.InputToggle"/> and
    /// <see cref="OwnRuleKind.InputEdge"/> for relays 1–8 on the (B) and (C): any other gets
    /// <see cref="ArgumentOutOfRangeException"/>, and a model without modes <see cref="NotSupportedException"/>, before
    /// anything is sent. A relay has one mode, so the rules for a relay are taken together. Returns the rules whose relay
    /// the board took, each once, in relay order; none, and nothing sent, for none asked.
    /// </summary>
    public async Task<IReadOnlyList<OwnRule>> TurnOffOwnRulesAsync(IReadOnlyCollection<OwnRule> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (!Capabilities.OwnRulesOrCantTell.TurnsOff)
        {
            throw new NotSupportedException($"The {_client.Model.Name} has no relay control modes.");
        }

        List<OwnRule> asked = [];
        foreach (var rule in rules)
        {
            if (rule is null)
            {
                throw new ArgumentException("A rule can't be null.", nameof(rules));
            }

            if (rule.Kind is not (OwnRuleKind.InputLinkage or OwnRuleKind.InputToggle or OwnRuleKind.InputEdge))
            {
                throw new ArgumentOutOfRangeException(nameof(rules), rule.Kind, "A Waveshare board's own rules are its relays' control modes.");
            }

            if (rule.Relay > WaveshareApi.RelayModeCount || rule.Relay > Capabilities.RelayCount)
            {
                throw new ArgumentOutOfRangeException(nameof(rules), rule.Relay, $"The {_client.Model.Name} has relay modes for relays 1–{WaveshareApi.RelayModeCount} only.");
            }

            if (!asked.Contains(rule))
            {
                asked.Add(rule);
            }
        }

        var mask = ChannelMask.Of(asked.Select(r => r.Relay));
        if (mask.IsEmpty)
        {
            return [];
        }

        var taken = new ChannelMask(await _client.TurnOffModesAsync(mask.Bits, cancellationToken).ConfigureAwait(false));
        return [.. asked.Where(r => taken.Contains(r.Relay)).OrderBy(r => r.Relay).ThenBy(r => r.Kind)];
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _client.Refresh();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The board's state as a <see cref="BoardState"/>, with its own rules (<see cref="ToOwnRules"/>): null while the
    /// modes aren't known (a model without them, before they were read, or a read that failed), and kept while
    /// disconnected, as the relays are. While they aren't known, the relays the last read that worked showed in linkage
    /// mode stay unavailable (<see cref="BoardState.KeptUnavailable"/>, issue #567).
    /// </summary>
    public static BoardState ToBoardState(WaveshareState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new BoardState(
            state.IsConnected,
            new ChannelMask(state.Relays),
            new ChannelMask(state.Inputs),
            state.LastContactUtc,
            OwnRules: state.Modes is { } modes ? ToOwnRules(modes) : null,
            KeptUnavailable: state.Modes is null ? new ChannelMask(state.Linkage) : ChannelMask.None);
    }

    /// <summary>
    /// The relays' control modes as rules, in relay order; relay n's mode is tied to input n. Mode 1 (linkage: the relay
    /// follows its input, and the board ignores commands for it) is <see cref="OwnRuleKind.InputLinkage"/>, 2 (toggle:
    /// an input pulse flips it) <see cref="OwnRuleKind.InputToggle"/>, 3 (edge: each input change flips it)
    /// <see cref="OwnRuleKind.InputEdge"/>; 0 (normal) is no rule. Source: the (B)/(C) wiki, §1.5 and §5.3.7.
    /// </summary>
    public static OwnRules ToOwnRules(IReadOnlyList<ushort> modes)
    {
        ArgumentNullException.ThrowIfNull(modes);
        List<OwnRule> rules = [];
        for (var i = 0; i < modes.Count && i < WaveshareApi.RelayModeCount; i++)
        {
            OwnRuleKind? kind = modes[i] switch
            {
                WaveshareApi.ModeLinkage => OwnRuleKind.InputLinkage,
                WaveshareApi.ModeToggle => OwnRuleKind.InputToggle,
                WaveshareApi.ModeEdge => OwnRuleKind.InputEdge,
                _ => null,
            };
            if (kind is { } k)
            {
                rules.Add(new OwnRule(i + 1, k));
            }
        }

        return rules.Count == 0 ? OwnRules.Empty : new OwnRules(rules);
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

    private void OnStateChanged(object? sender, WaveshareState state)
    {
        Raise(StateChanged, state);
    }

    // Only the modes changed: not a change the safety services, which listen to StateChanged, must see.
    private void OnOwnRulesChanged(object? sender, WaveshareState state)
    {
        Raise(OwnRulesChanged, state);
    }

    // Each subscriber on its own, as for the other boards: one that fails must not keep the state from the others.
    private void Raise(EventHandler<BoardState>? handlers, WaveshareState state)
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
