using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Contactor.Boards.Waveshare;

/// <summary>
/// Talks to a Waveshare Modbus PoE ETH relay over Modbus RTU over TCP (<see cref="WaveshareApi"/>): polls its relays
/// (FC 01) and inputs (FC 02) through <see cref="BoardPoller{TReading}"/> and switches relays one at a time (FC 05). One
/// TCP connection and one request at a time (the poller sees to it), each waiting for its answer, which must come
/// within <see cref="RequestTimeout"/>. A request without an answer or with an unreadable one closes the connection,
/// since what comes next can't be trusted to be in step, and the next request opens a new one. A request on a
/// connection from before that turns out broken (the board may close one it found idle) goes once more on a new one.
/// Commands are never queued: without a connection they fail at once with <see cref="BoardNotConnectedException"/>, a
/// poll under way gives way to them, and the board's state right after them is reported at once. Modbus has no login.
/// On the (B) and (C) it also reads the relays' control modes (FC 03), at each connection and then every
/// <see cref="ModesInterval"/>, and never writes them on its own: only <see cref="TurnOffModesAsync"/>, on a person's
/// press, sets one relay at a time back to normal (FC 06).
/// </summary>
public sealed class WaveshareClient : IPolledBoard<WaveshareStatus>, IDisposable, IAsyncDisposable
{
    /// <summary>How long a request may take before it counts as unanswered; also the limit to connect.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How often the relays' control modes are read again on the (B) and (C), after the read at connecting.</summary>
    public static readonly TimeSpan ModesInterval = TimeSpan.FromSeconds(60);

    // Bytes waiting before a request were sent unasked; more than this and the connection is opened afresh.
    private const int MaxSkippedBytes = 4096;

    private readonly BoardModel _model;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WaveshareClient> _logger;
    private readonly BoardPoller<WaveshareStatus> _poller;

    private BoardEndpoint _endpoint;

    // The connection; only used under the poller's lock (a connect, a poll or a command), and by Dispose.
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private WaveshareStatus? _lastTraced;
    private int _wasConnected;

    // Set by Dispose: no connection is opened after it, also by a request still under way.
    private int _disposed;

    // The relays' control modes as last read, and when; the time is null before the first read on the connection open
    // now. Only under the poller's lock (a connect, a poll or a command).
    private IReadOnlyList<ushort>? _modes;
    private DateTimeOffset? _modesReadUtc;

    // The relays the board confirmed set back to normal (the echo of FC 06, TurnOffModesAsync) since the last read of the
    // modes that worked: the board said they left linkage mode, so they are taken out of the kept linkage relays while
    // the modes can't be read (issue #567). A read that works clears it. Only under the poller's lock.
    private ulong _linkageCleared;

    public WaveshareClient(IOptions<WaveshareOptions> options, BoardModel model, TimeProvider timeProvider, ILogger<WaveshareClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        if (!WaveshareModels.IsWaveshare(model))
        {
            throw new ArgumentException($"{model.Name} isn't a Waveshare board.", nameof(model));
        }

        var settings = options.Value;
        if (settings.PollSeconds < 1)
        {
            throw new ArgumentException("Waveshare:PollSeconds must be at least 1.", nameof(options));
        }

        _model = model;
        _endpoint = new BoardEndpoint(settings.Host, settings.Port);
        _timeProvider = timeProvider;
        _logger = logger;
        _poller = new BoardPoller<WaveshareStatus>(this, timeProvider, WaveshareStatus.SwitchesComparer)
        {
            PollInterval = TimeSpan.FromSeconds(settings.PollSeconds),
        };
    }

    /// <summary>
    /// Raised when the relays or the inputs change, and when the connection is lost or comes back; not on a poll that
    /// changed nothing. On the poller's loop: handlers must be quick.
    /// </summary>
    public event EventHandler<WaveshareState>? StateChanged;

    /// <summary>Every request and answer and every connection step, in plain words. Handlers must be quick.</summary>
    public event EventHandler<BoardTraceEntry>? Traced;

    /// <summary>
    /// Raised when only the relays' control modes changed (<see cref="WaveshareState.Modes"/>), not the relays, the
    /// inputs or the connection, which come as <see cref="StateChanged"/>. On the poller's loop: handlers must be quick.
    /// </summary>
    public event EventHandler<WaveshareState>? OwnRulesChanged;

    /// <summary>The model it talks to: how many relays and inputs to read.</summary>
    public BoardModel Model => _model;

    /// <summary>The latest state; refreshed on every poll, so its last contact is always current.</summary>
    public WaveshareState State
    {
        get
        {
            var state = _poller.State;
            return new WaveshareState(state.IsConnected, state.Reading, state.LastContactUtc);
        }
    }

    /// <summary>Where the client connects: <c>Waveshare:Host</c>/<c>Port</c> until <see cref="SetEndpoint"/> moves it.</summary>
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
        return CommandAsync(mask, relay => WaveshareModbus.WriteSingleCoil(WaveshareApi.UnitId, (ushort)(relay - 1), on: true), cancellationToken);
    }

    /// <summary>Turns off the relays in <paramref name="mask"/> (bit 0 = relay 1), one request each.</summary>
    public Task<ulong> TurnOffAsync(ulong mask, CancellationToken cancellationToken = default)
    {
        return CommandAsync(mask, relay => WaveshareModbus.WriteSingleCoil(WaveshareApi.UnitId, (ushort)(relay - 1), on: false), cancellationToken);
    }

    /// <summary>
    /// Pulses the relays in <paramref name="mask"/> (bit 0 = relay 1), one "flash on" request each, as
    /// <see cref="TurnOnAsync"/>: the board turns each on, and off again by itself after <paramref name="length"/>.
    /// Throws <see cref="ArgumentOutOfRangeException"/>, before anything is sent, for a length
    /// <see cref="WaveshareModels.Pulse"/> can't do.
    /// </summary>
    public Task<ulong> PulseAsync(ulong mask, TimeSpan length, CancellationToken cancellationToken = default)
    {
        if (!WaveshareModels.Pulse.CanDo(length))
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "A Waveshare board pulses for 0.1 s to 3276.7 s, in steps of 0.1 s.");
        }

        return CommandAsync(mask, relay => WaveshareModbus.FlashOn(WaveshareApi.UnitId, relay, length), cancellationToken);
    }

    /// <summary>
    /// Sets the control mode of the relays in <paramref name="mask"/> (bit 0 = relay 1) back to normal, as a person
    /// asked: one FC 06 per relay, writing 0 to its own register, never the others'. Every relay in the mask is sent
    /// for, also when the board refuses one, and the modes are read back in the same command, so the state after it
    /// shows what the board did. A relay whose write the board echoed is no longer kept in linkage mode while the modes
    /// can't be read (<see cref="WaveshareStatus.Linkage"/>). Returns the relays the board took; throws as <see cref="TurnOnAsync"/> does when it
    /// took none. Throws <see cref="NotSupportedException"/> on a model without modes, before anything is sent.
    /// </summary>
    public Task<ulong> TurnOffModesAsync(ulong mask, CancellationToken cancellationToken = default)
    {
        if (!WaveshareModels.HasRelayModes(_model))
        {
            throw new NotSupportedException($"The {_model.Name} has no relay control modes.");
        }

        if ((mask >> WaveshareApi.RelayModeCount) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mask), mask, $"The {_model.Name} has relay modes for relays 1–{WaveshareApi.RelayModeCount} only.");
        }

        return CommandAsync(
            mask,
            relay => WaveshareModbus.WriteSingleRegister(WaveshareApi.UnitId, (ushort)(WaveshareApi.RelayModeRegister + relay - 1), WaveshareApi.ModeNormal),
            cancellationToken,
            readModes: true);
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

    // Only opens the connection: the first poll reads the modes, as on any new connection. Nothing is written.
    async Task IPolledBoard<WaveshareStatus>.ConnectAsync(CancellationToken cancellationToken)
    {
        _modes = null;
        _modesReadUtc = null;
        await OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    Task<WaveshareStatus> IPolledBoard<WaveshareStatus>.ReadAsync(CancellationToken cancellationToken)
    {
        return ReadStatusAsync(cancellationToken);
    }

    void IPolledBoard<WaveshareStatus>.OnStateChanged(PolledState<WaveshareStatus> state)
    {
        var wasConnected = Interlocked.Exchange(ref _wasConnected, state.IsConnected ? 1 : 0) == 1;
        if (state.IsConnected && !wasConnected)
        {
            var endpoint = Endpoint;
            _logger.Connected(endpoint.Host, endpoint.Port);
        }

        if (StateChanged is not { } handlers)
        {
            return;
        }

        var waveshareState = new WaveshareState(state.IsConnected, state.Reading, state.LastContactUtc);

        // Each subscriber on its own: one that fails must not keep the state from the others.
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<WaveshareState>>())
        {
            try
            {
                handler(this, waveshareState);
            }
#pragma warning disable CA1031 // A subscriber's bug must not take down the poller or the other subscribers.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.SubscriberFailed(ex);
            }
        }
    }

    // The comparer leaves out only the modes: that is what changed.
    void IPolledBoard<WaveshareStatus>.OnReadingChanged(PolledState<WaveshareStatus> state)
    {
        Raise(OwnRulesChanged, new WaveshareState(state.IsConnected, state.Reading, state.LastContactUtc));
    }

    void IPolledBoard<WaveshareStatus>.OnStep(BoardPollStep kind, string? detail)
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

    // Sends request(relay) for every relay in the mask: an ON, an OFF, a pulse or a mode back to normal (readModes: the
    // read after it reads the modes too, and a relay whose write the board echoed leaves the kept linkage relays).
    private async Task<ulong> CommandAsync(ulong mask, Func<int, WaveshareRequest> request, CancellationToken cancellationToken, bool readModes = false)
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
            await _poller.RunSwitchesAsync(token => SwitchEachAsync(mask, request, taken, readModes, token), cancellationToken).ConfigureAwait(false);
            return taken.Value;
        }
        catch (Exception ex) when (IsNoAnswer(ex))
        {
            // The poller counted it as a failed poll and reads the board at once, which shows what it did.
            throw new BoardNotConnectedException("The Waveshare board didn't answer; the command may not have been carried out.", ex);
        }
        catch (Exception ex) when (ex is WaveshareProtocolException or BoardRefusedException)
        {
            // No relay was taken. The poller read the board at once, and counted it as a failed poll unless the board
            // refused every relay (BoardRefusedException): that is an answer.
            throw new BoardNotConnectedException($"The Waveshare board didn't take the command: {ex.Message}", ex);
        }
    }

    // Under the poller's lock. Returns the board's state read after the switches, which the poller takes as a poll's,
    // or none when that read failed, and puts the relays the board took in <paramref name="taken"/>.
    private async Task<CommandReading<WaveshareStatus>> SwitchEachAsync(ulong mask, Func<int, WaveshareRequest> requestFor, StrongBox<ulong> taken, bool readModes, CancellationToken cancellationToken)
    {
        string? refusal = null;
        var unreadable = false;
        foreach (var relay in new ChannelMask(mask).Channels)
        {
            // No answer (IsNoAnswer), also on the new connection ExchangeAsync tries after a broken one, is thrown as it
            // is and stops the rest: the poller counts it as a failed poll.
            try
            {
                var request = requestFor(relay);
                var echo = await ExchangeAsync(request, routine: false, cancellationToken).ConfigureAwait(false);
                Trace(BoardTraceDirection.Received, WaveshareTraceText.Confirmed(echo), WaveshareModbus.Hex(echo));
                taken.Value |= 1UL << (relay - 1);
                if (readModes)
                {
                    // A mode written to normal, and the board echoed it: the relay is no longer in linkage mode.
                    _linkageCleared |= 1UL << (relay - 1);
                }
            }
            catch (Exception ex) when (ex is WaveshareRefusedException or WaveshareProtocolException)
            {
                // Refused, or an answer that can't be read: the relay may or may not have switched. The others still
                // go; the read after them shows the truth.
                refusal ??= ex.Message;
                unreadable |= ex is WaveshareProtocolException;
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
            throw unreadable ? new WaveshareProtocolException(refusal!) : new BoardRefusedException(refusal!);
        }

        try
        {
            return new CommandReading<WaveshareStatus>(await ReadStatusAsync(cancellationToken, readModes).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is WaveshareRefusedException or WaveshareProtocolException || IsNoAnswer(ex))
        {
            // Never a state the board didn't read: an echo of FC 05 doesn't show that the relay switched (a relay in
            // linkage mode echoes and stays as it is), and a group would take a made-up "on" as confirmation. The
            // command doesn't fail (what was taken was sent), and it isn't an answered poll: the poller keeps the last
            // state read and reads the board at once, which counts as a poll of its own if it fails too.
            return default;
        }
    }

    // The relays and inputs, and on the (B) and (C) the modes when due: at the first read on a connection, every
    // ModesInterval after, and when readModes asks (after writing one).
    private async Task<WaveshareStatus> ReadStatusAsync(CancellationToken cancellationToken, bool readModes = false)
    {
        var last = Volatile.Read(ref _lastTraced);

        // The modes first, so a relay's state is read after its mode: a relay that just left linkage mode shows as it is.
        if (WaveshareModels.HasRelayModes(_model)
            && (readModes || _modesReadUtc is not { } readUtc || _timeProvider.GetUtcNow() - readUtc >= ModesInterval))
        {
            _modes = await ReadModesAsync(last?.Modes, cancellationToken).ConfigureAwait(false);
            _modesReadUtc = _timeProvider.GetUtcNow();
            if (_modes is not null)
            {
                _linkageCleared = 0;
            }
        }

        var relayCount = _model.Capabilities.RelayCount;
        var relayAnswer = await ExchangeAsync(WaveshareModbus.ReadCoils(WaveshareApi.UnitId, 0, (ushort)relayCount), routine: true, cancellationToken)
            .ConfigureAwait(false);
        var relays = WaveshareModbus.Bits(relayAnswer, relayCount);
        Trace(BoardTraceDirection.Received, WaveshareTraceText.Relays(relays, relayCount), WaveshareModbus.Hex(relayAnswer), isRoutine: last?.Relays == relays);

        ulong inputs = 0;
        var inputCount = _model.Capabilities.InputCount;
        if (inputCount > 0)
        {
            var inputAnswer = await ExchangeAsync(WaveshareModbus.ReadDiscreteInputs(WaveshareApi.UnitId, 0, (ushort)inputCount), routine: true, cancellationToken)
                .ConfigureAwait(false);
            inputs = WaveshareModbus.Bits(inputAnswer, inputCount);
            Trace(BoardTraceDirection.Received, WaveshareTraceText.Inputs(inputs, inputCount), WaveshareModbus.Hex(inputAnswer), isRoutine: last?.Inputs == inputs);
        }

        // The relays in linkage mode: from the modes when they were read, otherwise as the last reading that knew them
        // (kept while disconnected, gone once the poller forgets the board), less those the board confirmed set back to
        // normal since: a read that failed says nothing about them (issue #567).
        var linkage = _modes is { } modes ? Linkage(modes) : (_poller.State.Reading?.Linkage ?? 0) & ~_linkageCleared;
        var status = new WaveshareStatus(relays, inputs, _modes, linkage);
        Volatile.Write(ref _lastTraced, status);
        return status;
    }

    // Bit per relay whose mode is linkage, bit 0 = relay 1.
    private static ulong Linkage(IReadOnlyList<ushort> modes)
    {
        ulong mask = 0;
        for (var i = 0; i < modes.Count && i < WaveshareApi.RelayModeCount; i++)
        {
            if (modes[i] == WaveshareApi.ModeLinkage)
            {
                mask |= 1UL << i;
            }
        }

        return mask;
    }

    // The 8 mode registers, read with one FC 03; never written here. A read the board refuses, or whose answer can't be
    // read or holds a mode the manual doesn't name, gives null: unknown, never a guess. No answer at all is thrown, and
    // fails the poll as any other read's would.
    private async Task<IReadOnlyList<ushort>?> ReadModesAsync(IReadOnlyList<ushort>? last, CancellationToken cancellationToken)
    {
        try
        {
            var read = WaveshareModbus.ReadHoldingRegisters(WaveshareApi.UnitId, WaveshareApi.RelayModeRegister, WaveshareApi.RelayModeCount);
            var answer = await ExchangeAsync(read, routine: true, cancellationToken).ConfigureAwait(false);
            var modes = WaveshareModbus.Registers(answer);
            if (modes.Length != WaveshareApi.RelayModeCount || modes.Any(m => m > WaveshareApi.ModeEdge))
            {
                Trace(BoardTraceDirection.Received, $"Relay modes the app doesn't know: {string.Join(",", modes)}", WaveshareModbus.Hex(answer), isError: true);
                return null;
            }

            var unchanged = last is not null && last.SequenceEqual(modes);
            Trace(BoardTraceDirection.Received, WaveshareTraceText.Modes(modes), WaveshareModbus.Hex(answer), isRoutine: unchanged);
            return modes;
        }
        catch (Exception ex) when (ex is WaveshareRefusedException or WaveshareProtocolException)
        {
            Trace(BoardTraceDirection.None, $"Couldn't read the relay modes: {ex.Message}", isError: true);
            return null;
        }
    }

    // A request that got no answer at all: it counts as a failed poll, whether a poll or a command sent it.
    private static bool IsNoAnswer(Exception exception)
    {
        return exception is TimeoutException or IOException or SocketException;
    }

    /// <summary>
    /// Sends one request and reads its answer, within <see cref="RequestTimeout"/>, on the open connection or a new one.
    /// Traces the request and anything but a good answer, which the caller traces as it knows what it says. Closes
    /// the connection when the answer doesn't come or can't be read. When the connection was open from before (not one
    /// opened while connecting) and turns out broken (the board may close one it found idle) before any of the answer
    /// came, the request goes once more on a new connection: every request the client sends does the same when done
    /// twice (reading, switching one relay, starting its pulse again, writing one relay's mode to normal).
    /// </summary>
    private async Task<byte[]> ExchangeAsync(WaveshareRequest request, bool routine, CancellationToken cancellationToken)
    {
        // A connection open before this request (the board may have closed it): a broken one gets the request once more.
        var reused = _stream is not null;
        try
        {
            // None after an earlier request closed it (no answer, or an unreadable one).
            if (_stream is null)
            {
                await OpenAsync(cancellationToken).ConfigureAwait(false);
            }

            SkipUnasked();
        }
        catch (Exception ex) when (IsNoAnswer(ex))
        {
            Close();
            Trace(BoardTraceDirection.None, $"No answer: {ex.Message}", isError: true);
            if (reused)
            {
                // Closed above, so the request goes on a new connection, and only once more: a new one isn't reused.
                return await ExchangeAsync(request, routine, cancellationToken).ConfigureAwait(false);
            }

            throw;
        }

        var stream = _stream!;
        var reader = new CountingStream(stream);
        Trace(BoardTraceDirection.Sent, WaveshareTraceText.Request(request.Frame), WaveshareModbus.Hex(request.Frame), isRoutine: routine);
        using var timeout = new CancellationTokenSource(RequestTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await stream.WriteAsync(request.Frame, linked.Token).ConfigureAwait(false);
            return await WaveshareModbus.ReadAnswerAsync(reader, request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Close();
            var message = $"The Waveshare board did not answer within {RequestTimeout.TotalSeconds} s.";
            Trace(BoardTraceDirection.None, $"No answer: {message}", isError: true);
            throw new TimeoutException(message);
        }
        catch (OperationCanceledException)
        {
            // A command took the poll's place, or the app stops: the answer may still come, out of step.
            Close();
            throw;
        }
        catch (WaveshareRefusedException ex)
        {
            // A whole answer: the connection is still in step.
            Trace(BoardTraceDirection.Received, WaveshareTraceText.Refused(ex.Code), WaveshareModbus.Hex([.. ex.Frame]), isError: true);
            throw;
        }
        catch (WaveshareProtocolException ex)
        {
            Close();
            Trace(BoardTraceDirection.Received, $"Unreadable answer: {ex.Message}", WaveshareModbus.Hex([.. ex.Received]), isError: true);
            throw;
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            // One failed poll doesn't lose the connection, so it shows here.
            Close();
            Trace(BoardTraceDirection.None, $"No answer: {ex.Message}", isError: true);
            if (reused && reader.BytesRead == 0)
            {
                // Closed above, so the request goes on a new connection, and only once more: a new one isn't reused.
                return await ExchangeAsync(request, routine, cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
    }

    // Opens a new connection to the board, closing any before. Throws TimeoutException, SocketException or IOException,
    // also once the client is disposed.
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
            throw new TimeoutException($"The Waveshare board did not take a connection within {RequestTimeout.TotalSeconds} s.");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        _stream = tcp.GetStream();
        Interlocked.Exchange(ref _tcp, tcp);

        // Each new connection reads the modes afresh with its next status: the board may have been changed meanwhile.
        _modesReadUtc = null;

        // Disposed while connecting: Dispose's Close may have come before the connection was kept.
        if (Volatile.Read(ref _disposed) == 1)
        {
            Close();
            ThrowIfDisposed();
        }
    }

    // Once disposed, a request still under way gets no answer: it fails as one, and the command throws
    // BoardNotConnectedException as for any request without an answer.
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

        if (available <= 0)
        {
            return;
        }

        if (available > MaxSkippedBytes)
        {
            Close();
            throw new IOException(string.Create(CultureInfo.InvariantCulture, $"The board sent {available} bytes nobody asked for."));
        }

        var skipped = new byte[available];
        var read = _stream!.Read(skipped);
        Trace(
            BoardTraceDirection.Received,
            string.Create(CultureInfo.InvariantCulture, $"Skipped {read} bytes nobody asked for"),
            WaveshareModbus.Hex(skipped.AsSpan(0, read)),
            isError: true);
    }

    private void Close()
    {
        var tcp = Interlocked.Exchange(ref _tcp, null);
        _stream = null;
        tcp?.Dispose();
    }

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
#pragma warning disable CA1031 // A subscriber's bug must not take down the connection.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.SubscriberFailed(ex);
            }
        }
    }

    // The first status after a new connection always gets a toast.
    private void ForgetLastStatus()
    {
        Volatile.Write(ref _lastTraced, null);
    }

    private void Trace(BoardTraceDirection direction, string text, string? bytes = null, bool isError = false, bool isRoutine = false)
    {
        if (Traced is not { } handlers)
        {
            return;
        }

        var entry = new BoardTraceEntry(_timeProvider.GetUtcNow(), direction, text, bytes, isError, isRoutine);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<BoardTraceEntry>>())
        {
            try
            {
                handler(this, entry);
            }
#pragma warning disable CA1031 // A subscriber's bug must not take down the poller.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.SubscriberFailed(ex);
            }
        }
    }
}
