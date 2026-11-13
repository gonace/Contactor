using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Contactor.Boards.Waveshare;

/// <summary>
/// Talks to a Waveshare Modbus PoE ETH relay over Modbus RTU over TCP (<see cref="WaveshareApi"/>): polls its relays
/// (FC 01) and inputs (FC 02) through <see cref="BoardPoller{TReading}"/> and switches relays one at a time (FC 05). One
/// TCP connection and one request at a time (the poller sees to it), each waiting for its answer, which must come
/// within <see cref="RequestTimeout"/>. A request without an answer or with an unreadable one closes the connection,
/// since what comes next can't be trusted to be in step, and the next request opens a new one. Commands are never
/// queued: without a connection they fail at once with <see cref="BoardNotConnectedException"/>, a poll under way gives
/// way to them, and the board's state right after them is reported at once. Modbus has no login.
/// </summary>
public sealed class WaveshareClient : IPolledBoard<WaveshareStatus>, IDisposable
{
    /// <summary>How long a request may take before it counts as unanswered; also the limit to connect.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    // Bytes waiting before a request were sent unasked; more than this and the connection is opened afresh.
    private const int MaxSkippedBytes = 4096;

    private readonly BoardModel _model;
    private readonly WaveshareOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WaveshareClient> _logger;
    private readonly BoardPoller<WaveshareStatus> _poller;

    private BoardEndpoint _endpoint;

    // The connection; only used under the poller's lock (a connect, a poll or a command), and by Dispose.
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private WaveshareStatus? _lastTraced;
    private int _wasConnected;

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
        _options = settings;
        _endpoint = new BoardEndpoint(settings.Host, settings.Port);
        _timeProvider = timeProvider;
        _logger = logger;
        _poller = new BoardPoller<WaveshareStatus>(this, timeProvider)
        {
            PollInterval = TimeSpan.FromSeconds(settings.PollSeconds),
            IsNoAnswer = IsNoAnswer,
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
    /// Raised when connecting found a relay not in normal mode and set every relay to normal mode
    /// (<see cref="WaveshareOptions.ResetRelayModes"/>). On the poller's loop, before the first poll: handlers must be
    /// quick.
    /// </summary>
    public event EventHandler<WaveshareRelayModesReset>? RelayModesReset;

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
    public Task TurnOnAsync(ulong mask, CancellationToken cancellationToken = default)
    {
        return CommandAsync(mask, turnOn: true, cancellationToken);
    }

    /// <summary>Turns off the relays in <paramref name="mask"/> (bit 0 = relay 1), one request each.</summary>
    public Task TurnOffAsync(ulong mask, CancellationToken cancellationToken = default)
    {
        return CommandAsync(mask, turnOn: false, cancellationToken);
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

    public void Dispose()
    {
        Close();
        _poller.Dispose();
    }

    async Task IPolledBoard<WaveshareStatus>.ConnectAsync(CancellationToken cancellationToken)
    {
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        if (_options.ResetRelayModes && WaveshareModels.HasRelayModes(_model))
        {
            await ResetRelayModesAsync(cancellationToken).ConfigureAwait(false);
        }
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

    private async Task CommandAsync(ulong mask, bool turnOn, CancellationToken cancellationToken)
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
            return;
        }

        // A poll under way gives way. The board switches one relay per request, and the whole mask goes out, as one
        // command on a board that takes a mask would: a group's interlock and "All off" rely on every OFF being sent.
        // Only a request that gets no answer at all (the board is gone) stops it.
        try
        {
            await _poller.RunCommandAsync(token => SwitchEachAsync(mask, turnOn, token), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNoAnswer(ex))
        {
            // The poller counted a missing answer as a failed poll and reads the board at once, which shows what it
            // did.
            throw new BoardNotConnectedException("The Waveshare board didn't answer; the command may not have been carried out.", ex);
        }
    }

    // Under the poller's lock. Returns the board's state after the switches, which the poller takes as a poll's.
    private async Task<WaveshareStatus> SwitchEachAsync(ulong mask, bool turnOn, CancellationToken cancellationToken)
    {
        ulong taken = 0;
        string? refusal = null;
        foreach (var relay in new ChannelMask(mask).Channels)
        {
            // No answer (IsNoAnswer) is thrown as it is, so the poller counts it as a failed poll.
            try
            {
                var request = WaveshareModbus.WriteSingleCoil(WaveshareApi.UnitId, (ushort)(relay - 1), turnOn);
                var echo = await ExchangeAsync(request, routine: false, cancellationToken).ConfigureAwait(false);
                Trace(BoardTraceDirection.Received, WaveshareTraceText.Confirmed(echo), WaveshareModbus.Hex(echo));
                taken |= 1UL << (relay - 1);
            }
            catch (Exception ex) when (ex is WaveshareRefusedException or WaveshareProtocolException)
            {
                // Refused, or an answer that can't be read: the relay may or may not have switched. The others still
                // go; the read after them shows the truth.
                refusal ??= ex.Message;
                Trace(
                    BoardTraceDirection.None,
                    string.Create(CultureInfo.InvariantCulture, $"The board didn't take the command for relay {relay}: {ex.Message}"),
                    isError: true);
            }
        }

        // Not one relay was taken: the command failed, as on a board that takes the mask at once, and nothing is
        // counted as switched. The poller reads the board at once, which shows what really happened.
        if (taken == 0)
        {
            throw new BoardNotConnectedException($"The Waveshare board didn't take the command: {refusal}");
        }

        try
        {
            return await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WaveshareRefusedException or WaveshareProtocolException)
        {
            // Never a state the board didn't read: an echo of FC 05 doesn't show that the relay switched (a relay in
            // linkage mode echoes and stays as it is), and a group would take a made-up "on" as confirmation. The last
            // state read stays, and a read at once shows the truth.
            _poller.PollNow();
            return _poller.State.Reading ?? throw new BoardNotConnectedException("The Waveshare board's state after the command couldn't be read.");
        }
    }

    private async Task<WaveshareStatus> ReadStatusAsync(CancellationToken cancellationToken)
    {
        var last = Volatile.Read(ref _lastTraced);
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

        var status = new WaveshareStatus(relays, inputs);
        Volatile.Write(ref _lastTraced, status);
        return status;
    }

    // Reads the relay control modes, and sets them all to normal when one isn't. A refused or unreadable answer leaves
    // them as they are, with a warning: the board still works, only its inputs may switch relays past the app. No
    // answer fails the connection, which the poller tries again.
    private async Task ResetRelayModesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var read = WaveshareModbus.ReadHoldingRegisters(WaveshareApi.UnitId, WaveshareApi.RelayModeRegister, WaveshareApi.RelayModeCount);
            var answer = await ExchangeAsync(read, routine: false, cancellationToken).ConfigureAwait(false);
            var modes = WaveshareModbus.Registers(answer);
            Trace(BoardTraceDirection.Received, WaveshareTraceText.Modes(modes), WaveshareModbus.Hex(answer));
            if (modes.All(m => m == WaveshareApi.ModeNormal))
            {
                return;
            }

            var write = WaveshareModbus.WriteMultipleRegisters(WaveshareApi.UnitId, WaveshareApi.RelayModeRegister, new ushort[WaveshareApi.RelayModeCount]);
            var echo = await ExchangeAsync(write, routine: false, cancellationToken).ConfigureAwait(false);
            Trace(BoardTraceDirection.Received, WaveshareTraceText.Confirmed(echo), WaveshareModbus.Hex(echo));
            var found = string.Join(",", modes);
            _logger.RelayModesReset(found);
            RaiseRelayModesReset(new WaveshareRelayModesReset(modes, Endpoint));
        }
        catch (Exception ex) when (ex is WaveshareRefusedException or WaveshareProtocolException)
        {
            _logger.RelayModesNotReset(ex.Message);
            Trace(BoardTraceDirection.None, $"Couldn't set the relays to normal mode: {ex.Message}", isError: true);
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
    /// the connection when the answer doesn't come or can't be read.
    /// </summary>
    private async Task<byte[]> ExchangeAsync(WaveshareRequest request, bool routine, CancellationToken cancellationToken)
    {
        try
        {
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
            throw;
        }

        var stream = _stream!;
        Trace(BoardTraceDirection.Sent, WaveshareTraceText.Request(request.Frame), WaveshareModbus.Hex(request.Frame), isRoutine: routine);
        using var timeout = new CancellationTokenSource(RequestTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await stream.WriteAsync(request.Frame, linked.Token).ConfigureAwait(false);
            return await WaveshareModbus.ReadAnswerAsync(stream, request, linked.Token).ConfigureAwait(false);
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
            throw;
        }
    }

    // Opens a new connection to the board, closing any before. Throws TimeoutException, SocketException or IOException.
    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        Close();
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

        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    // Nothing comes unasked from the board; bytes waiting now are left from an answer that came too late or were sent
    // by something else. They are dropped, or with too many of them the connection is opened afresh at the next request.
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

    private void RaiseRelayModesReset(WaveshareRelayModesReset reset)
    {
        if (RelayModesReset is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<WaveshareRelayModesReset>>())
        {
            try
            {
                handler(this, reset);
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
