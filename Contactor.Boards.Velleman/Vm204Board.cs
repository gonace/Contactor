using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Velleman;

/// <summary>
/// The VM204 as an <see cref="IRelayBoard"/>: 4 relays, 4 digital inputs and an analog input, which it never reports
/// by itself, so <see cref="Vm204Client"/> polls it. This only translates the client's state and commands. The analog
/// input comes with every status; a change in it alone comes as <see cref="AnalogChanged"/>.
/// </summary>
public sealed class Vm204Board : IRelayBoard
{
    /// <summary>
    /// The analog input: 10 bit, 1023 at 6.6 V at the terminal. The firmware (V1.1, <c>Main/analog.c</c>) reads it as
    /// <c>n / 1023 × 3.3 × 2</c>; the manual (December 2016, "D. Analog threshold", p. 19) says 6.6 V "=1024", one count
    /// off. The firmware's is what the board's own page shows, so the app shows the same.
    /// </summary>
    public static readonly AnalogAbility Vm204Analog = new(Vm204Api.AnalogInputCount, Vm204Api.AnalogMaxCounts, Vm204Api.AnalogFullScaleVolts);

    public static readonly BoardCapabilities Vm204Capabilities = new(
        Vm204Api.RelayCount,
        InputCount: Vm204Api.InputCount,
        ReportsChanges: false,
        Analog: Vm204Analog,
        OwnRules: OwnRulesAbility.None);

    private readonly Vm204Client _client;
    private readonly ILogger<Vm204Board> _logger;

    // The analog readings the latest event of either kind carried. Both come under the poller's lock, one at a time.
    private AnalogReadings? _lastAnalog;

    public Vm204Board(Vm204Client client, ILogger<Vm204Board> logger)
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

    // No pulse yet (Issue #182).
    public Task<ChannelMask> PulseAsync(ChannelMask relays, TimeSpan length, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This board can't pulse a relay.");
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _client.Refresh();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The VM204's state as a <see cref="BoardState"/>. Its analog input is unknown (null) before the first answer; an
    /// answer without it, or with a value outside 0–1023, has no reading for it.
    /// </summary>
    public static BoardState ToBoardState(Vm204State state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var analog = state.Status is { } status ? new AnalogReadings([Vm204Analog.Check(status.Analog)]) : null;
        return new BoardState(state.IsConnected, new ChannelMask(state.Relays), new ChannelMask(state.Inputs), state.LastContactUtc, state.Problem, analog);
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

    private void OnStateChanged(object? sender, Vm204State state)
    {
        var boardState = ToBoardState(state);
        _lastAnalog = boardState.Analog;
        Raise(StateChanged, boardState);
    }

    // A raw value that changed between two the converter can't give (1500 to 1600), or a field that went away after
    // one, reads the same: no reading. Nothing to tell then.
    private void OnAnalogChanged(object? sender, Vm204State state)
    {
        var boardState = ToBoardState(state);
        if (Equals(boardState.Analog, _lastAnalog))
        {
            return;
        }

        _lastAnalog = boardState.Analog;
        Raise(AnalogChanged, boardState);
    }

    // Each subscriber on its own, as for the VM201: one that fails must not keep the state from the others.
    private void Raise(EventHandler<BoardState>? handlers, BoardState boardState)
    {
        if (handlers is null)
        {
            return;
        }

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
                Vm204Log.SubscriberFailed(_logger, ex);
            }
        }
    }
}
