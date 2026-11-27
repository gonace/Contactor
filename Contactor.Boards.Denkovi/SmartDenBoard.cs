using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Denkovi;

/// <summary>
/// A Denkovi smartDEN IP-16R-MT as an <see cref="IRelayBoard"/>: its model's relays, which it never reports by itself,
/// so <see cref="SmartDenClient"/> polls it. Its own rules are its week schedule's rows (<see cref="ToOwnRules"/>), which
/// the app reads and turns off only on a person's press. This only translates the client's state and commands.
/// </summary>
public sealed class SmartDenBoard : IRelayBoard
{
    private readonly SmartDenClient _client;
    private readonly ILogger<SmartDenBoard> _logger;

    public SmartDenBoard(SmartDenClient client, ILogger<SmartDenBoard> logger)
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
        if (!SmartDenModels.Pulse.CanDo(length))
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, SmartDenModbus.PulseRange);
        }

        return new ChannelMask(await _client.PulseAsync(ToMask(relays), length, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Turns off the week schedule's rows behind the <see cref="OwnRuleKind.Timer"/> rules, one FC 06 to each row's enable
    /// flag, and reads the schedule back (<see cref="SmartDenClient.TurnOffOwnRulesAsync"/>).
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
    /// The board's state as a <see cref="BoardState"/>, with its own rules (<see cref="ToOwnRules"/>): null while the
    /// week schedule isn't known (before it was read, or a read that failed), and kept while disconnected, as the relays
    /// are.
    /// </summary>
    public static BoardState ToBoardState(SmartDenState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new BoardState(
            state.IsConnected,
            new ChannelMask(state.Relays),
            ChannelMask.None,
            state.LastContactUtc,
            OwnRules: state.Schedule is { } schedule ? ToOwnRules(schedule) : null);
    }

    /// <summary>
    /// The week schedule as rules: a <see cref="OwnRuleKind.Timer"/> rule on each relay a row switches, in relay order,
    /// and one unit per row (its relays' timers), as a row is turned off whole (section 8.9: a row switches "a group of
    /// relays"). A row that turns its relays off counts as one that turns them on: both switch without the app.
    /// </summary>
    public static OwnRules ToOwnRules(SmartDenSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var relays = new ChannelMask(schedule.Rows.Aggregate(0UL, (mask, row) => mask | row.Relays));
        if (relays.IsEmpty)
        {
            return OwnRules.Empty;
        }

        List<OwnRule> rules = [.. relays.Channels.Select(r => new OwnRule(r, OwnRuleKind.Timer))];
        List<IReadOnlyCollection<OwnRule>> units = [.. schedule.Rows.Select(row => (IReadOnlyCollection<OwnRule>)[.. new ChannelMask(row.Relays).Channels.Select(r => new OwnRule(r, OwnRuleKind.Timer))])];
        return new OwnRules(rules, units);
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

    private void OnStateChanged(object? sender, SmartDenState state)
    {
        Raise(StateChanged, state);
    }

    // Only the week schedule changed: not a change the safety services, which listen to StateChanged, must see.
    private void OnOwnRulesChanged(object? sender, SmartDenState state)
    {
        Raise(OwnRulesChanged, state);
    }

    // Each subscriber on its own, as for the other boards: one that fails must not keep the state from the others.
    private void Raise(EventHandler<BoardState>? handlers, SmartDenState state)
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
