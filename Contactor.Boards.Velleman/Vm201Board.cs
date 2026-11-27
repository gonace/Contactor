using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Velleman;

/// <summary>
/// The VM201 as an <see cref="IRelayBoard"/>: 8 relays and one input, which it reports by itself on every change, and
/// a pulse of 1 s to 99 h, and its own timer programs, which its status reports and the app can turn off. The
/// connection itself is <see cref="Vm201Client"/>'s; this only translates its state and commands.
/// </summary>
public sealed class Vm201Board : IRelayBoard
{
    public static readonly BoardCapabilities Vm201Capabilities = new(
        Vm201Channel.Count, InputCount: 1, ReportsChanges: true, Pulse: Vm201Pulse.Ability, OwnRules: OwnRulesAbility.ReadsAndTurnsOff);

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

    /// <summary>
    /// Pulses the relays, all at once, as <see cref="TurnOnAsync"/>. Only a length the board can send exactly
    /// (<see cref="Vm201Pulse.TryEncode"/>): any other, such as 100 s, gets <see cref="ArgumentOutOfRangeException"/>.
    /// The board's <see cref="PulseAbility.CanDo"/> says no to those lengths too (<see cref="Vm201Pulse.Ability"/>).
    /// </summary>
    public async Task<ChannelMask> PulseAsync(ChannelMask relays, TimeSpan length, CancellationToken cancellationToken = default)
    {
        var mask = ToMask(relays);
        await _client.PulseAsync(mask, length, cancellationToken).ConfigureAwait(false);
        return relays;
    }

    /// <summary>
    /// Turns off the board's own timer programs for the relays of <paramref name="rules"/>, all in one CMD_TMR_DIS with
    /// only their bits, as <see cref="TurnOnAsync"/>: the board takes the mask at once, or the command fails. Only
    /// <see cref="OwnRuleKind.Timer"/> rules for relays 1–8: any other gets <see cref="ArgumentOutOfRangeException"/>
    /// before anything is sent. A rule given twice is sent once. Returns the rules sent, in relay order; none, and nothing
    /// sent, for none asked.
    /// </summary>
    public async Task<OwnRulesTurnOff> TurnOffOwnRulesAsync(IReadOnlyCollection<OwnRule> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var relays = ChannelMask.None;
        foreach (var rule in rules)
        {
            if (rule is null)
            {
                throw new ArgumentException("A rule can't be null.", nameof(rules));
            }

            if (rule.Kind != OwnRuleKind.Timer)
            {
                throw new ArgumentOutOfRangeException(nameof(rules), rule.Kind, "The VM201 has timer programs only.");
            }

            if (rule.Relay > Vm201Channel.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(rules), rule.Relay, "The VM201 has relays 1–8 only.");
            }

            relays |= ChannelMask.Of(rule.Relay);
        }

        var mask = ToMask(relays);
        if (mask == 0)
        {
            return OwnRulesTurnOff.None;
        }

        await _client.TurnOffTimersAsync(mask, cancellationToken).ConfigureAwait(false);
        return OwnRulesTurnOff.Of(ToOwnRules(mask));
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        return _client.RequestStatusAsync(cancellationToken);
    }

    /// <summary>
    /// The VM201's own status as a <see cref="BoardState"/>; its one input is input 1. Its own rules are its timer
    /// programs (status byte 2), a <see cref="OwnRuleKind.Timer"/> per relay, in relay order: null before its first
    /// status (also at a new address), and kept while disconnected, as the relays are.
    /// </summary>
    public static BoardState ToBoardState(Vm201State state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new BoardState(
            state.IsConnected,
            new ChannelMask(state.Outputs),
            state.Input ? ChannelMask.Of(1) : ChannelMask.None,
            state.LastContactUtc,
            state.Problem,
            OwnRules: state.LastContactUtc is null ? null : ToOwnRules(state.TimerMask));
    }

    /// <summary>A timer rule for each relay whose bit is set in the board's timer mask, in relay order.</summary>
    public static OwnRules ToOwnRules(byte timerMask)
    {
        return timerMask == 0
            ? OwnRules.Empty
            : new OwnRules(new ChannelMask(timerMask).Channels.Select(relay => new OwnRule(relay, OwnRuleKind.Timer)));
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
                Vm201Log.SubscriberFailed(_logger, ex);
            }
        }
    }
}
