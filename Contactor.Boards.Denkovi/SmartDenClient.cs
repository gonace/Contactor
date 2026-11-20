using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Contactor.Boards.Denkovi;

/// <summary>
/// Talks to a Denkovi smartDEN IP-16R-MT over Modbus TCP (<see cref="SmartDenApi"/>): polls its relays (FC 01) through
/// <see cref="BoardPoller{TReading}"/>, switches relays one at a time (FC 05) and pulses them (FC 06). One TCP
/// connection (the board takes one at a time) and one request at a time (the poller sees to it), each waiting for its
/// answer, which must come within <see cref="RequestTimeout"/>. A request without an answer or with an unreadable one
/// closes the connection, since what comes next can't be trusted to be in step, and the next request opens a new one.
/// A poll that a command cancels keeps the connection, as the board may refuse a new one while it holds the old: its
/// late answer is skipped by its transaction id.
/// Commands are never queued: without a connection they fail at once with <see cref="BoardNotConnectedException"/>, a
/// poll under way gives way to them, and the board's state right after them is reported at once. Modbus has no login.
/// It also reads the board's week schedule (two FC 03), at the first poll on each connection and then every
/// <see cref="RulesInterval"/>, and never writes it on its own: only <see cref="TurnOffOwnRulesAsync"/>, on a person's
/// press, turns rows off (FC 06 at a row's enable flag).
/// </summary>
public sealed class SmartDenClient : IPolledBoard<SmartDenStatus>, IDisposable, IAsyncDisposable
{
    /// <summary>How long a request may take before it counts as unanswered; also the limit to connect.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How often the week schedule is read again, after the read at the first poll on a connection.</summary>
    public static readonly TimeSpan RulesInterval = TimeSpan.FromSeconds(60);

    // Bytes waiting before a request were sent unasked; more than this and the connection is opened afresh.
    private const int MaxSkippedBytes = 4096;

    // Requests given up on whose answer may still come; more than this and the connection is opened afresh.
    private const int MaxLateAnswers = 8;

    private readonly BoardModel _model;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SmartDenClient> _logger;
    private readonly BoardPoller<SmartDenStatus> _poller;

    // The transaction ids of requests on this connection that a command cancelled: their answers may still come. Only
    // used under the poller's lock, as the connection.
    private readonly HashSet<ushort> _late = [];

    private BoardEndpoint _endpoint;

    // The connection; only used under the poller's lock (a connect, a poll or a command), and by Dispose.
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private SmartDenStatus? _lastTraced;
    private int _wasConnected;
    private int _disposed;
    private int _transaction;

    // The week schedule as last read on this connection (null: unknown), and when; the time is null before the first read
    // on the connection open now. Only under the poller's lock (a connect, a poll or a command).
    private SmartDenSchedule? _schedule;
    private DateTimeOffset? _scheduleReadUtc;

    public SmartDenClient(IOptions<SmartDenOptions> options, BoardModel model, TimeProvider timeProvider, ILogger<SmartDenClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        if (!SmartDenModels.IsSmartDen(model))
        {
            throw new ArgumentException($"{model.Name} isn't a smartDEN board.", nameof(model));
        }

        var settings = options.Value;
        if (settings.PollSeconds < 1)
        {
            throw new ArgumentException("SmartDen:PollSeconds must be at least 1.", nameof(options));
        }

        _model = model;
        _endpoint = new BoardEndpoint(settings.Host, settings.Port);
        _timeProvider = timeProvider;
        _logger = logger;
        _poller = new BoardPoller<SmartDenStatus>(this, timeProvider, SmartDenStatus.RelaysComparer)
        {
            PollInterval = TimeSpan.FromSeconds(settings.PollSeconds),
        };
    }

    /// <summary>
    /// Raised when the relays change, and when the connection is lost or comes back; not on a poll that changed
    /// nothing. On the poller's loop: handlers must be quick.
    /// </summary>
    public event EventHandler<SmartDenState>? StateChanged;

    /// <summary>Every request and answer and every connection step, in plain words. Handlers must be quick.</summary>
    public event EventHandler<BoardTraceEntry>? Traced;

    /// <summary>
    /// Raised when only the week schedule changed (<see cref="SmartDenState.Schedule"/>), not the relays or the
    /// connection, which come as <see cref="StateChanged"/>. On the poller's loop: handlers must be quick.
    /// </summary>
    public event EventHandler<SmartDenState>? OwnRulesChanged;

    /// <summary>The model it talks to: how many relays to read.</summary>
    public BoardModel Model => _model;

    /// <summary>The latest state; refreshed on every poll, so its last contact is always current.</summary>
    public SmartDenState State
    {
        get
        {
            var state = _poller.State;
            return new SmartDenState(state.IsConnected, state.Reading, state.LastContactUtc);
        }
    }

    /// <summary>Where the client connects: <c>SmartDen:Host</c>/<c>Port</c> until <see cref="SetEndpoint"/> moves it.</summary>
    public BoardEndpoint Endpoint => Volatile.Read(ref _endpoint);

    /// <summary>Moves to another address at once; nothing happens if it is the same.</summary>
    public void SetEndpoint(BoardEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (Interlocked.Exchange(ref _endpoint, endpoint) == endpoint)
        {
            return;
        }

        _logger.EndpointChanged(endpoint.Host, endpoint.Port);

        // Another board may be at the new address: nothing is known about it until it answers.
        _poller.Forget();
        _poller.Restart();
    }

    /// <summary>Polls until cancelled. Never throws because of the board. Call it once.</summary>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        return _poller.RunAsync(cancellationToken);
    }

    /// <summary>Turns on the relays in <paramref name="mask"/> (bit 0 = relay 1), one request each.</summary>
    public Task<ulong> TurnOnAsync(ulong mask, CancellationToken cancellationToken = default)
    {
        return CommandAsync(mask, relay => SmartDenModbus.WriteSingleCoil(NextTransaction(), relay, on: true), cancellationToken);
    }

    /// <summary>Turns off the relays in <paramref name="mask"/> (bit 0 = relay 1), one request each.</summary>
    public Task<ulong> TurnOffAsync(ulong mask, CancellationToken cancellationToken = default)
    {
        return CommandAsync(mask, relay => SmartDenModbus.WriteSingleCoil(NextTransaction(), relay, on: false), cancellationToken);
    }

    /// <summary>
    /// Pulses the relays in <paramref name="mask"/> (bit 0 = relay 1), one pulse request each, as
    /// <see cref="TurnOnAsync"/>: the board turns each on, and off again by itself after <paramref name="length"/>.
    /// Throws <see cref="ArgumentOutOfRangeException"/>, before anything is sent, for a length
    /// <see cref="SmartDenModels.Pulse"/> can't do.
    /// </summary>
    public Task<ulong> PulseAsync(ulong mask, TimeSpan length, CancellationToken cancellationToken = default)
    {
        if (!SmartDenModels.Pulse.CanDo(length))
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, SmartDenModbus.PulseRange);
        }

        return CommandAsync(mask, relay => SmartDenModbus.Pulse(NextTransaction(), relay, length), cancellationToken);
    }

    /// <summary>
    /// Turns off the board's own <paramref name="rules"/>, as a person asked, and reads the week schedule back in the same
    /// command. Each <see cref="OwnRuleKind.Timer"/> rule turns off every row that switches its relay as last read, with
    /// one FC 06 writing 0 to that row's enable flag, each row once: the other relays such a row switches lose their
    /// timer too, as <see cref="OwnRules.GoesWith(IEnumerable{OwnRule})"/> says. A row's outputs code, state and time are
    /// never written. A refused write or one whose answer can't be read doesn't stop the others. A rule given twice is
    /// taken once. Returns the rules asked whose rows all took it (also when, as last read, no row switches its relay any
    /// more), in relay order; none, and nothing sent, for none asked. Throws <see cref="ArgumentOutOfRangeException"/>
    /// for another kind of rule or a relay the board doesn't have, and <see cref="BoardNotConnectedException"/> without a
    /// connection, both before anything is sent; and <see cref="BoardNotConnectedException"/> when the schedule isn't
    /// known (nothing sent), the board took none of them or didn't answer.
    /// </summary>
    public async Task<IReadOnlyList<OwnRule>> TurnOffOwnRulesAsync(IReadOnlyCollection<OwnRule> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        List<OwnRule> asked = [];
        foreach (var rule in rules)
        {
            if (rule is null)
            {
                throw new ArgumentException("A rule can't be null.", nameof(rules));
            }

            if (rule.Kind != OwnRuleKind.Timer)
            {
                throw new ArgumentOutOfRangeException(nameof(rules), rule.Kind, "The smartDEN's own rules are its week schedule's rows.");
            }

            if (rule.Relay > _model.Capabilities.RelayCount)
            {
                throw new ArgumentOutOfRangeException(nameof(rules), rule.Relay, $"The {_model.Name} has relays 1–{_model.Capabilities.RelayCount} only.");
            }

            if (!asked.Contains(rule))
            {
                asked.Add(rule);
            }
        }

        if (asked.Count == 0)
        {
            return [];
        }

        if (!State.IsConnected)
        {
            throw new BoardNotConnectedException();
        }

        try
        {
            var taken = new StrongBox<IReadOnlyList<OwnRule>>([]);
            await _poller.RunSwitchesAsync(token => TurnOffRowsAndReadAsync(asked, taken, token), cancellationToken).ConfigureAwait(false);
            return taken.Value ?? [];
        }
        catch (Exception ex) when (IsNoAnswer(ex))
        {
            // The poller counted it as a failed poll and reads the board at once.
            throw new BoardNotConnectedException("The smartDEN board didn't answer; its week schedule may not have been turned off.", ex);
        }
        catch (Exception ex) when (ex is SmartDenProtocolException or BoardRefusedException)
        {
            throw new BoardNotConnectedException($"The smartDEN board didn't turn its week schedule off: {ex.Message}", ex);
        }
    }

    /// <summary>Polls at once; the answer comes as <see cref="StateChanged"/> if anything changed.</summary>
    public void Refresh()
    {
        if (!State.IsConnected)
        {
            throw new BoardNotConnectedException();
        }

        _poller.PollNow();
    }

    /// <summary>
    /// Ends the connection once the poll or command under way has ended (at most the poller's
    /// <see cref="BoardPoller{TReading}.DisposeWait"/>): nothing new starts, and nothing is left using the connection
    /// when it closes. A command that outlasts the wait throws <see cref="BoardNotConnectedException"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _poller.DisposeAsync().ConfigureAwait(false);
        Dispose();
    }

    public void Dispose()
    {
        // The poller first, so no new poll or command starts; then nothing opens a new connection. A request already
        // under way goes on until Close breaks its connection under it, and then fails as unanswered.
        _poller.Dispose();
        Interlocked.Exchange(ref _disposed, 1);
        Close();
    }

    // Only opens the connection: the first poll reads the week schedule, as on any new connection. Nothing is written.
    Task IPolledBoard<SmartDenStatus>.ConnectAsync(CancellationToken cancellationToken)
    {
        _schedule = null;
        _scheduleReadUtc = null;
        return OpenAsync(cancellationToken);
    }

    Task<SmartDenStatus> IPolledBoard<SmartDenStatus>.ReadAsync(CancellationToken cancellationToken)
    {
        return ReadStatusAsync(ScheduleRead.IfDue, cancellationToken);
    }

    // The comparer leaves out only the schedule: that is what changed.
    void IPolledBoard<SmartDenStatus>.OnReadingChanged(PolledState<SmartDenStatus> state)
    {
        Raise(OwnRulesChanged, new SmartDenState(state.IsConnected, state.Reading, state.LastContactUtc));
    }

    void IPolledBoard<SmartDenStatus>.OnStateChanged(PolledState<SmartDenStatus> state)
    {
        var wasConnected = Interlocked.Exchange(ref _wasConnected, state.IsConnected ? 1 : 0) == 1;
        if (state.IsConnected && !wasConnected)
        {
            var endpoint = Endpoint;
            _logger.Connected(endpoint.Host, endpoint.Port);
        }

        Raise(StateChanged, new SmartDenState(state.IsConnected, state.Reading, state.LastContactUtc));
    }

    void IPolledBoard<SmartDenStatus>.OnStep(BoardPollStep kind, string? detail)
    {
        switch (kind)
        {
            case BoardPollStep.Connecting:
                ForgetLastStatus();
                Trace(BoardTraceDirection.None, $"Connecting to {Endpoint}");
                break;
            case BoardPollStep.Lost:
                ForgetLastStatus();
                _logger.ConnectionLost(detail);
                Trace(BoardTraceDirection.None, $"Connection lost: {detail}", isError: true);
                break;
            case BoardPollStep.Waiting:
                _logger.Reconnecting(detail);
                Trace(BoardTraceDirection.None, $"Trying again in {detail} s");
                break;
            case BoardPollStep.Stopped:
                ForgetLastStatus();
                Trace(BoardTraceDirection.None, "The app closed the connection");
                break;
            default:
                break;
        }
    }

    // A request that got no answer at all: it counts as a failed poll, whether a poll or a command sent it.
    private static bool IsNoAnswer(Exception exception)
    {
        return exception is TimeoutException or IOException or SocketException;
    }

    // The seconds of RequestTimeout as a message says them: "5".
    private static string TimeoutSeconds => RequestTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture);

    // The next transaction id; it wraps after 65535.
    private ushort NextTransaction()
    {
        return unchecked((ushort)Interlocked.Increment(ref _transaction));
    }

    // Sends request(relay) for every relay in the mask: an ON, an OFF or a pulse.
    private async Task<ulong> CommandAsync(ulong mask, Func<int, SmartDenRequest> request, CancellationToken cancellationToken)
    {
        if (!_model.Capabilities.AllRelays.ContainsAll(new ChannelMask(mask)))
        {
            throw new ArgumentOutOfRangeException(nameof(mask), mask, $"The {_model.Name} has relays 1–{_model.Capabilities.RelayCount} only.");
        }

        if (!State.IsConnected)
        {
            throw new BoardNotConnectedException();
        }

        if (mask == 0)
        {
            return 0;
        }

        // A poll under way gives way. The board switches one relay per request, and the whole mask goes out, as one
        // command on a board that takes a mask would: a group's interlock and "All off" rely on every OFF being sent.
        // Only a request that gets no answer at all, also on a new connection (the board is gone), stops it.
        try
        {
            var taken = new StrongBox<ulong>();
            await _poller.RunSwitchesAsync(token => SwitchEachAsync(mask, request, taken, token), cancellationToken).ConfigureAwait(false);
            return taken.Value;
        }
        catch (Exception ex) when (IsNoAnswer(ex))
        {
            // The poller counted it as a failed poll and reads the board at once, which shows what it did.
            throw new BoardNotConnectedException("The smartDEN board didn't answer; the command may not have been carried out.", ex);
        }
        catch (Exception ex) when (ex is SmartDenProtocolException or BoardRefusedException)
        {
            // No relay was taken. The poller read the board at once, and counted it as a failed poll unless the board
            // refused every relay (BoardRefusedException): that is an answer. Outside the poller a refusal is "didn't
            // take the command", as on every board.
            throw new BoardNotConnectedException($"The smartDEN board didn't take the command: {ex.Message}", ex);
        }
    }

    // Under the poller's lock. Returns the board's state read after the switches, which the poller takes as a poll's,
    // or none when that read failed, and puts the relays the board took in <paramref name="taken"/>.
    private async Task<CommandReading<SmartDenStatus>> SwitchEachAsync(ulong mask, Func<int, SmartDenRequest> requestFor, StrongBox<ulong> taken, CancellationToken cancellationToken)
    {
        string? refusal = null;
        var unreadable = false;
        foreach (var relay in new ChannelMask(mask).Channels)
        {
            // No answer (IsNoAnswer), also on the new connection ExchangeAsync tries after a broken one, is thrown as it
            // is and stops the rest: the poller counts it as a failed poll.
            try
            {
                var answer = await ExchangeAsync(requestFor(relay), routine: false, cancellationToken).ConfigureAwait(false);
                Trace(BoardTraceDirection.Received, SmartDenTraceText.Confirmed(answer), SmartDenModbus.Hex(answer));
                taken.Value |= 1UL << (relay - 1);
            }
            catch (Exception ex) when (ex is SmartDenRefusedException or SmartDenProtocolException)
            {
                // Refused, or an answer that can't be read: the relay may or may not have switched. The others still
                // go, the next on a new connection after an unreadable answer; the read after them shows the truth.
                refusal ??= ex.Message;
                unreadable |= ex is SmartDenProtocolException;
                Trace(
                    BoardTraceDirection.None,
                    string.Create(CultureInfo.InvariantCulture, $"The board didn't take the command for relay {relay}: {ex.Message}"),
                    isError: true);
            }
        }

        // Not one relay was taken: the command failed, as on a board that takes the mask at once, and nothing is
        // counted as switched. The poller reads the board at once, which shows what really happened. When the board
        // refused every relay with an exception answer, it answered: that doesn't count toward losing it. When an
        // answer couldn't be read, it counts as a failed poll, as a poll whose answer it can't read would.
        if (taken.Value == 0)
        {
            throw unreadable ? new SmartDenProtocolException(refusal!) : new BoardRefusedException(refusal!);
        }

        try
        {
            // Not the schedule: a group's next step waits for this read.
            return new CommandReading<SmartDenStatus>(await ReadStatusAsync(ScheduleRead.Never, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is SmartDenRefusedException or SmartDenProtocolException || IsNoAnswer(ex))
        {
            // Never a state the board didn't read: an echo doesn't show that the relay switched. The command doesn't
            // fail (what was taken was sent), and it isn't an answered poll: the poller keeps the last state read and
            // reads the board at once, which counts as a poll of its own if it fails too.
            return default;
        }
    }

    // Under the poller's lock: one FC 06 per row behind the timers asked, then the board's state read back with the
    // schedule. Puts the rules the board took in taken.
    private async Task<CommandReading<SmartDenStatus>> TurnOffRowsAndReadAsync(IReadOnlyList<OwnRule> asked, StrongBox<IReadOnlyList<OwnRule>> taken, CancellationToken cancellationToken)
    {
        // The schedule as last read, which the person saw.
        if (_schedule is not { } schedule)
        {
            // Nothing went out: not an answer, and not a failure of the board.
            throw new BoardNotConnectedException("The smartDEN board's week schedule isn't known yet; nothing was sent.");
        }

        // Whatever comes of it, the next read reads the schedule again.
        _scheduleReadUtc = null;

        List<SmartDenRow> rows = [.. asked.SelectMany(r => schedule.Switching(r.Relay)).Distinct().OrderBy(r => r.Row)];
        HashSet<int> off = [];
        string? refusal = null;
        var unreadable = false;
        foreach (var row in rows)
        {
            // No answer, also on the new connection ExchangeAsync tries after a broken one, is thrown as it is and stops
            // the rest: the poller counts it as a failed poll.
            try
            {
                var answer = await ExchangeAsync(SmartDenModbus.WriteRowEnable(NextTransaction(), row.Row, enable: false), routine: false, cancellationToken)
                    .ConfigureAwait(false);
                Trace(BoardTraceDirection.Received, SmartDenTraceText.Confirmed(answer), SmartDenModbus.Hex(answer));
                off.Add(row.Row);
            }
            catch (Exception ex) when (ex is SmartDenRefusedException or SmartDenProtocolException)
            {
                // The others still go, the next on a new connection after an unreadable answer; the read after them
                // shows what the board did.
                refusal ??= ex.Message;
                unreadable |= ex is SmartDenProtocolException;
                Trace(
                    BoardTraceDirection.None,
                    string.Create(CultureInfo.InvariantCulture, $"The board didn't turn off week schedule row {row.Row}: {ex.Message}"),
                    isError: true);
            }
        }

        List<OwnRule> done = [.. asked.Where(r => schedule.Switching(r.Relay).All(row => off.Contains(row.Row)))];
        if (done.Count == 0)
        {
            throw unreadable ? new SmartDenProtocolException(refusal!) : new BoardRefusedException(refusal!);
        }

        taken.Value = [.. done.OrderBy(r => r.Relay)];
        try
        {
            return new CommandReading<SmartDenStatus>(await ReadStatusAsync(ScheduleRead.Now, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is SmartDenRefusedException or SmartDenProtocolException || IsNoAnswer(ex))
        {
            // The writes went out; the poller keeps the last state and reads again at once, the schedule included.
            return default;
        }
    }

    // The relays, and after them the week schedule when it is asked for or due: at the first read on a connection and
    // every RulesInterval after. The relays first, so a schedule read that fails can't keep them from the poll.
    private async Task<SmartDenStatus> ReadStatusAsync(ScheduleRead schedule, CancellationToken cancellationToken)
    {
        var last = Volatile.Read(ref _lastTraced);
        var relayCount = _model.Capabilities.RelayCount;
        var answer = await ExchangeAsync(SmartDenModbus.ReadCoils(NextTransaction(), 0, (ushort)relayCount), routine: true, cancellationToken)
            .ConfigureAwait(false);
        var relays = SmartDenModbus.Bits(answer, relayCount);
        Trace(BoardTraceDirection.Received, SmartDenTraceText.Relays(relays, relayCount), SmartDenModbus.Hex(answer), isRoutine: last?.Relays == relays);

        if (schedule == ScheduleRead.Now
            || (schedule == ScheduleRead.IfDue && (_scheduleReadUtc is not { } readUtc || _timeProvider.GetUtcNow() - readUtc >= RulesInterval)))
        {
            _schedule = await ReadScheduleAsync(cancellationToken).ConfigureAwait(false);
            _scheduleReadUtc = _timeProvider.GetUtcNow();
        }

        var status = new SmartDenStatus(relays, _schedule);
        Volatile.Write(ref _lastTraced, status);
        return status;
    }

    // The week schedule: each row's enable flag and outputs code, one FC 03 of 30 registers each, as a read must stay in
    // one block (section 10.2.1.3); never written here. Never fails the poll: a read the board refuses, an answer that
    // can't be read or holds an enable flag the manual doesn't name (0 or 1), or no answer at all, gives null: unknown,
    // never a guess: the relays read before it still make the poll. The next request opens a new connection when this
    // one was closed. Only a cancelled read (a command takes the poll's place, or the app stops) is thrown.
    private async Task<SmartDenSchedule?> ReadScheduleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var enabled = await ReadRegistersAsync(SmartDenApi.RowEnableRegister, cancellationToken).ConfigureAwait(false);
            if (enabled is null)
            {
                return null;
            }

            var outputs = await ReadRegistersAsync(SmartDenApi.RowOutputsRegister, cancellationToken).ConfigureAwait(false);
            if (outputs is null)
            {
                return null;
            }

            var schedule = SmartDenSchedule.From(enabled.Value.Registers, outputs.Value.Registers);
            Trace(
                BoardTraceDirection.Received,
                SmartDenTraceText.Schedule(schedule),
                $"{enabled.Value.Hex} / {outputs.Value.Hex}",
                isRoutine: _schedule == schedule);
            return schedule;
        }
        catch (Exception ex) when (ex is SmartDenRefusedException or SmartDenProtocolException || IsNoAnswer(ex))
        {
            // A refusal or no answer is traced by ExchangeAsync; this says what it means.
            Trace(BoardTraceDirection.None, $"Couldn't read the week schedule: {ex.Message}", isError: true);
            return null;
        }
    }

    // One block of 30 registers, one per row; null for an enable flag other than 0 or 1.
    private async Task<(ushort[] Registers, string Hex)?> ReadRegistersAsync(ushort address, CancellationToken cancellationToken)
    {
        var answer = await ExchangeAsync(SmartDenModbus.ReadHoldingRegisters(NextTransaction(), address, SmartDenApi.ScheduleRows), routine: true, cancellationToken)
            .ConfigureAwait(false);
        var registers = SmartDenModbus.Registers(answer);
        if (address == SmartDenApi.RowEnableRegister && registers.Any(r => r > SmartDenApi.RowEnabled))
        {
            Trace(
                BoardTraceDirection.Received,
                $"Week schedule enable flags the manual doesn't name: {string.Join(",", registers)}",
                SmartDenModbus.Hex(answer),
                isError: true);
            return null;
        }

        return (registers, SmartDenModbus.Hex(answer));
    }

    /// <summary>
    /// Sends one request and reads its answer, within <see cref="RequestTimeout"/>, on the open connection or a new one.
    /// Traces the request and anything but a good answer, which the caller traces as it knows what it says. Closes
    /// the connection when the answer doesn't come or can't be read. When the connection was open from before and turns
    /// out broken (the board may close one it found idle), the request goes once more on a new connection: reading the
    /// relays or the week schedule, switching one relay, starting its pulse or turning a schedule row off does the same
    /// when done twice.
    /// </summary>
    private async Task<byte[]> ExchangeAsync(SmartDenRequest request, bool routine, CancellationToken cancellationToken, bool again = true)
    {
        var reused = _stream is not null;
        try
        {
            // None after an earlier request closed it (no answer, or an unreadable one).
            if (!reused)
            {
                await OpenAsync(cancellationToken).ConfigureAwait(false);
            }

            SkipUnasked();
        }
        catch (Exception ex) when (IsNoAnswer(ex))
        {
            Close();
            Trace(BoardTraceDirection.None, $"No answer: {ex.Message}", isError: true);
            if (reused && again)
            {
                return await ExchangeAsync(request, routine, cancellationToken, again: false).ConfigureAwait(false);
            }

            throw;
        }

        var stream = _stream!;
        var reader = new CountingStream(stream);
        Trace(BoardTraceDirection.Sent, SmartDenTraceText.Request(request.Frame), SmartDenModbus.Hex(request.Frame), isRoutine: routine);
        using var timeout = new CancellationTokenSource(RequestTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            // The frame goes whole or not at all: only the timeout stops it, never a command taking the poll's place.
            await stream.WriteAsync(request.Frame, timeout.Token).ConfigureAwait(false);
            return await SmartDenModbus.ReadAnswerAsync(reader, request, linked.Token, _late, SkippedLate).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Close();
            var message = $"The smartDEN board did not answer within {TimeoutSeconds} s.";
            Trace(BoardTraceDirection.None, $"No answer: {message}", isError: true);
            throw new TimeoutException(message);
        }
        catch (OperationCanceledException)
        {
            // A command took the poll's place, or the app stops. The connection stays: the board takes one at a time,
            // and may refuse a new one while it holds this. The answer may still come; the next request skips it by its
            // transaction id. Only when part of it was read already, or too many are given up on, is the connection
            // out of step and opened afresh.
            if (reader.BytesRead > 0 || _late.Count >= MaxLateAnswers)
            {
                Close();
            }
            else
            {
                _late.Add(request.TransactionId);
            }

            throw;
        }
        catch (SmartDenRefusedException ex)
        {
            // A whole answer: the connection is still in step.
            Trace(BoardTraceDirection.Received, SmartDenTraceText.Refused(ex.Code), SmartDenModbus.Hex([.. ex.Frame]), isError: true);
            throw;
        }
        catch (SmartDenProtocolException ex)
        {
            Close();
            Trace(BoardTraceDirection.Received, $"Unreadable answer: {ex.Message}", SmartDenModbus.Hex([.. ex.Received]), isError: true);
            throw;
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            // One failed poll doesn't lose the connection, so it shows here.
            Close();
            Trace(BoardTraceDirection.None, $"No answer: {ex.Message}", isError: true);
            if (reused && again && reader.BytesRead == 0)
            {
                return await ExchangeAsync(request, routine, cancellationToken, again: false).ConfigureAwait(false);
            }

            throw;
        }
    }

    // The answer to a request given up on, which came after all: dropped.
    private void SkippedLate(byte[] frame)
    {
        Trace(BoardTraceDirection.Received, "Skipped a late answer nobody waited for", SmartDenModbus.Hex(frame), isRoutine: true);
    }

    // Opens a new connection to the board, closing any before. Throws TimeoutException, SocketException or IOException.
    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        Close();
        ThrowIfDisposed();
        var endpoint = Endpoint;
        var tcp = new TcpClient { NoDelay = true };
        using var timeout = new CancellationTokenSource(RequestTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await tcp.ConnectAsync(endpoint.Host, endpoint.Port, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException($"The smartDEN board did not take a connection within {TimeoutSeconds} s.");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        // Kept with a full fence before the check below, against Dispose's flag before its Close: either that Close
        // finds this connection, or the check finds the flag.
        _stream = tcp.GetStream();
        Interlocked.Exchange(ref _tcp, tcp);

        // Disposed while connecting: Dispose's Close may have come before the connection was kept.
        if (Volatile.Read(ref _disposed) == 1)
        {
            Close();
            ThrowIfDisposed();
        }
    }

    // Once disposed, a request still under way gets no answer: it fails as one, and opens no new connection.
    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new IOException("The app closed the connection to the board.");
        }
    }

    // Nothing comes unasked from the board; bytes waiting now are left from an answer that came too late or were sent
    // by something else. They are dropped. With too many of them the connection is closed: a request on a connection
    // from before goes once more on a new one at once, and one on a new connection fails.
    private void SkipUnasked()
    {
        int available;
        try
        {
            available = _tcp?.Available ?? 0;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            Close();
            throw new IOException("The connection to the board broke.", ex);
        }

        // A late answer waits to be skipped by its transaction id (ExchangeAsync), not dropped half read.
        if (available <= 0 || _late.Count > 0)
        {
            return;
        }

        if (available > MaxSkippedBytes)
        {
            Close();
            throw new IOException(string.Create(CultureInfo.InvariantCulture, $"The board sent {available} bytes nobody asked for."));
        }

        var skipped = new byte[available];
        var read = _stream?.Read(skipped) ?? 0;
        Trace(
            BoardTraceDirection.Received,
            string.Create(CultureInfo.InvariantCulture, $"Skipped {read} bytes nobody asked for"),
            SmartDenModbus.Hex(skipped.AsSpan(0, read)),
            isError: true);
    }

    private void Close()
    {
        var tcp = Interlocked.Exchange(ref _tcp, null);
        _stream = null;
        _late.Clear();
        tcp?.Dispose();
    }

    // The first status after a new connection always gets a toast.
    private void ForgetLastStatus()
    {
        Volatile.Write(ref _lastTraced, null);
    }

    private void Trace(BoardTraceDirection direction, string text, string? bytes = null, bool isError = false, bool isRoutine = false)
    {
        Raise(Traced, new BoardTraceEntry(_timeProvider.GetUtcNow(), direction, text, bytes, isError, isRoutine));
    }

    // Which reads read the week schedule: a poll when it is due, a turn-off's read-back always, a switch's never.
    private enum ScheduleRead
    {
        IfDue,
        Now,
        Never,
    }

    // Each subscriber on its own: one that fails must not take down the poller or keep the news from the others.
    private void Raise<T>(EventHandler<T>? handlers, T value)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<T>>())
        {
            try
            {
                handler(this, value);
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
