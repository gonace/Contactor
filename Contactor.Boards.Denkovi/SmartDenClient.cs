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
/// </summary>
public sealed class SmartDenClient : IPolledBoard<SmartDenStatus>, IDisposable
{
    /// <summary>How long a request may take before it counts as unanswered; also the limit to connect.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

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
    private int _transaction;

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
        _poller = new BoardPoller<SmartDenStatus>(this, timeProvider)
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
        _poller.Dispose();
        Close();
    }

    Task IPolledBoard<SmartDenStatus>.ConnectAsync(CancellationToken cancellationToken)
    {
        return OpenAsync(cancellationToken);
    }

    Task<SmartDenStatus> IPolledBoard<SmartDenStatus>.ReadAsync(CancellationToken cancellationToken)
    {
        return ReadStatusAsync(cancellationToken);
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
            await _poller.RunCommandAsync(token => SwitchEachAsync(mask, request, taken, token), cancellationToken).ConfigureAwait(false);
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

    // Under the poller's lock. Returns the board's state after the switches, which the poller takes as a poll's, and
    // puts the relays the board took in <paramref name="taken"/>.
    private async Task<SmartDenStatus> SwitchEachAsync(ulong mask, Func<int, SmartDenRequest> requestFor, StrongBox<ulong> taken, CancellationToken cancellationToken)
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
            return await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SmartDenRefusedException or SmartDenProtocolException || IsNoAnswer(ex))
        {
            // Never a state the board didn't read: an echo doesn't show that the relay switched. The last state read
            // stays, and a read at once shows the truth; it counts as a poll of its own if it fails too. The command
            // doesn't fail (what was taken was sent), so the poller's read after a failed command doesn't come: ask
            // for it here.
            if (_poller.State.Reading is not { } last)
            {
                throw;
            }

            _poller.PollNow();
            return last;
        }
    }

    private async Task<SmartDenStatus> ReadStatusAsync(CancellationToken cancellationToken)
    {
        var last = Volatile.Read(ref _lastTraced);
        var relayCount = _model.Capabilities.RelayCount;
        var answer = await ExchangeAsync(SmartDenModbus.ReadCoils(NextTransaction(), 0, (ushort)relayCount), routine: true, cancellationToken)
            .ConfigureAwait(false);
        var relays = SmartDenModbus.Bits(answer, relayCount);
        Trace(BoardTraceDirection.Received, SmartDenTraceText.Relays(relays, relayCount), SmartDenModbus.Hex(answer), isRoutine: last?.Relays == relays);
        var status = new SmartDenStatus(relays);
        Volatile.Write(ref _lastTraced, status);
        return status;
    }

    /// <summary>
    /// Sends one request and reads its answer, within <see cref="RequestTimeout"/>, on the open connection or a new one.
    /// Traces the request and anything but a good answer, which the caller traces as it knows what it says. Closes
    /// the connection when the answer doesn't come or can't be read. When the connection was open from before and turns
    /// out broken (the board may close one it found idle), the request goes once more on a new connection: reading the
    /// relays, switching one or starting its pulse does the same when done twice.
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
            if (reused && again && ex is not TimeoutException)
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

    /// <summary>
    /// The connection's stream as the codec reads it, counting what it read: an answer read in part when a command
    /// takes the poll's place leaves the connection out of step.
    /// </summary>
    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;

        public CountingStream(Stream inner)
        {
            _inner = inner;
        }

        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesRead += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
