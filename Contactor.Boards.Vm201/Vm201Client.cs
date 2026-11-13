using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Contactor.Boards.Vm201;

/// <summary>
/// Holds exactly one connection to the VM201 board, logs in, tracks the status the board pushes and reconnects with
/// exponential backoff. Commands go through one channel per connection, so only one writer touches the socket.
/// Commands are never queued while disconnected: they fail at once with <see cref="BoardNotConnectedException"/>.
/// </summary>
public sealed class Vm201Client
{
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>The <see cref="Vm201TraceKind.ConnectionLost"/> detail when the app itself ended the connection.</summary>
    public const string StoppedByApp = "Closed by the app.";

    // A command waits here only while the writer is busy with the one before it.
    private const int CommandQueueCapacity = 32;

    private readonly Vm201Options _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<Vm201Client> _logger;
    private readonly TimeSpan _keepAlive;
    private readonly TimeSpan _idleTimeout;
    private Vm201State _state = Vm201State.Initial;

    // The state is written, and its change reported, under this lock, so a status from the old address can't overtake
    // SetEndpoint's reset.
    private readonly Lock _stateLock = new();
    private BoardEndpoint _endpoint;
    private Vm201Login _login;

    // The current connection attempt (connect, session and the wait after it); SetEndpoint cancels it to move at once.
    private CancellationTokenSource? _attempt;
    private Session? _session;
    private int _running;

    public Vm201Client(IOptions<Vm201Options> options, TimeProvider timeProvider, ILogger<Vm201Client> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;

        if (_options.KeepAliveSeconds < 1)
        {
            throw new ArgumentException("Vm201:KeepAliveSeconds must be at least 1.", nameof(options));
        }

        // Fail at start, not on the first connection, if the credentials cannot be sent.
        _login = Vm201Login.TryCreate(_options.Username, _options.Password, out var loginError)
            ?? throw new ArgumentException($"Vm201:Username or Vm201:Password can't be sent to the board: {loginError}", nameof(options));

        _endpoint = new BoardEndpoint(_options.Host, _options.Port);
        _keepAlive = TimeSpan.FromSeconds(_options.KeepAliveSeconds);
        _idleTimeout = _keepAlive * 2;
    }

    /// <summary>The board's last reported status.</summary>
    public Vm201State State => Volatile.Read(ref _state);

    /// <summary>Where the client connects: <c>Vm201:Host</c>/<c>Port</c> until <see cref="SetEndpoint"/> moves it.</summary>
    public BoardEndpoint Endpoint => Volatile.Read(ref _endpoint);

    /// <summary>The login it sends: <c>Vm201:Username</c>/<c>Password</c> until <see cref="SetLogin"/> changes it.</summary>
    public Vm201Login Login => Volatile.Read(ref _login);

    /// <summary>
    /// Uses another login from the next connection, and reconnects at once so it is tried straight away. Nothing
    /// happens if the login is the same.
    /// </summary>
    public void SetLogin(Vm201Login login)
    {
        ArgumentNullException.ThrowIfNull(login);
        if (Interlocked.Exchange(ref _login, login).Equals(login))
        {
            return;
        }

        _logger.LoginChanged(login.IsNone);
        Reconnect();
    }

    /// <summary>
    /// Moves the client to another board address: the current connection (or attempt, or wait before the next
    /// one) ends at once and the client connects to the new address. Nothing happens if the address is the same.
    /// </summary>
    public void SetEndpoint(BoardEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (Interlocked.Exchange(ref _endpoint, endpoint) == endpoint)
        {
            return;
        }

        _logger.EndpointChanged(endpoint.Host, endpoint.Port);

        // Another board may be at the new address: nothing is known about it until it answers, and a status from the
        // old one still on its way is dropped (ApplyStatus).
        SetState(Vm201State.Initial);
        Reconnect();
    }

    // Ends the current connection, attempt or wait, so the next one starts at once with what was just set.
    private void Reconnect()
    {
        try
        {
            Volatile.Read(ref _attempt)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // That attempt had just ended; the next one reads the new setting.
        }
    }

    /// <summary>
    /// Raised whenever <see cref="State"/> changes: on the connection's read loop for every status from the board
    /// (including the keep-alive) and when the connection is lost, and on the caller's thread when
    /// <see cref="SetEndpoint"/> forgets the state. Raised while a lock is held: handlers must be quick and must not
    /// block.
    /// </summary>
    public event EventHandler<Vm201State>? StateChanged;

    /// <summary>
    /// Raised for every packet to and from the board and every step of the connection (connecting, lost, the wait
    /// before the next attempt, skipped bytes), so a debugging view can show what the board does. A user name or
    /// password packet comes without its content. Raised on the connection's loops: handlers must be quick and must
    /// not block.
    /// </summary>
    public event EventHandler<Vm201TraceEntry>? Traced;

    public Task TurnOnAsync(byte mask, CancellationToken cancellationToken = default)
    {
        return SendAsync(Vm201PacketCodec.Encode(Vm201Command.On, mask), cancellationToken);
    }

    public Task TurnOffAsync(byte mask, CancellationToken cancellationToken = default)
    {
        return SendAsync(Vm201PacketCodec.Encode(Vm201Command.Off, mask), cancellationToken);
    }

    /// <summary>
    /// Pulses the relays in <paramref name="mask"/>: the board turns them on, and off again by itself after
    /// <paramref name="length"/>. Throws <see cref="ArgumentOutOfRangeException"/>, before anything is sent, for a length
    /// the board can't take exactly (<see cref="Vm201Pulse.TryEncode"/>).
    /// </summary>
    public Task PulseAsync(byte mask, TimeSpan length, CancellationToken cancellationToken = default)
    {
        if (!Vm201Pulse.TryEncode(length, out var time, out var unit))
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                "The VM201 pulses for whole seconds up to 99 s, whole minutes up to 99 min or whole hours up to 99 h.");
        }

        return SendAsync(Vm201PacketCodec.Encode(Vm201Command.Pulse, [mask, time, unit]), cancellationToken);
    }

    public Task RequestStatusAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(Vm201PacketCodec.Encode(Vm201Command.StatusRequest), cancellationToken);
    }

    public Task DisableBoardTimersAsync(byte mask, CancellationToken cancellationToken = default)
    {
        return SendAsync(Vm201PacketCodec.Encode(Vm201Command.TimerDisable, mask), cancellationToken);
    }

    /// <summary>
    /// Connects and keeps the connection open until <paramref name="cancellationToken"/> is cancelled. Never throws
    /// because of the board; it logs and reconnects. Call it once, e.g. from a hosted service.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            throw new InvalidOperationException("The VM201 client is already running.");
        }

        try
        {
            var backoff = MinBackoff;
            while (!cancellationToken.IsCancellationRequested)
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Volatile.Write(ref _attempt, attempt);
                var loggedIn = await RunSessionAsync(attempt.Token).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (loggedIn || attempt.IsCancellationRequested)
                {
                    backoff = MinBackoff;
                }

                // A new address: straight there, no wait.
                if (attempt.IsCancellationRequested)
                {
                    continue;
                }

                _logger.Reconnecting(backoff.TotalSeconds);
                Trace(Vm201TraceKind.Reconnecting, detail: ((int)backoff.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
                try
                {
                    await Task.Delay(backoff, _timeProvider, attempt.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // A new address during the wait.
                    backoff = MinBackoff;
                    continue;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                finally
                {
                    Volatile.Write(ref _attempt, null);
                }

                backoff = backoff * 2 > MaxBackoff ? MaxBackoff : backoff * 2;
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task SendAsync(byte[] packet, CancellationToken cancellationToken)
    {
        var session = Volatile.Read(ref _session);
        if (session is null || !State.IsConnected)
        {
            throw new BoardNotConnectedException();
        }

        var write = new PendingWrite(packet, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!session.Commands.Writer.TryWrite(write))
        {
            throw new BoardNotConnectedException("The VM201 connection is closing or busy; the command was not sent.");
        }

        await write.Completion!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One connection, from connect to close. Returns whether the login succeeded, which resets the backoff.
    /// </summary>
    private async Task<bool> RunSessionAsync(CancellationToken cancellationToken)
    {
        var loggedIn = false;
        Session? session = null;
        using var tcp = new TcpClient { NoDelay = true };
        var endpoint = Endpoint;
        try
        {
            _logger.Connecting(endpoint.Host, endpoint.Port);
            Trace(Vm201TraceKind.Connecting, detail: $"{endpoint.Host}:{endpoint.Port}");
            await WithTimeoutAsync(
                token => tcp.ConnectAsync(endpoint.Host, endpoint.Port, token).AsTask(),
                ConnectTimeout,
                "connect",
                cancellationToken).ConfigureAwait(false);

            var stream = tcp.GetStream();
            var decoder = new Vm201PacketDecoder(_logger);
            decoder.Discarded += (_, count) => Trace(
                Vm201TraceKind.DiscardedBytes,
                detail: count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var reader = new Vm201PacketReader(stream, decoder);
            await WithTimeoutAsync(
                token => LoginAsync(stream, reader, token),
                LoginTimeout,
                "log in",
                cancellationToken).ConfigureAwait(false);
            loggedIn = true;
            _logger.LoggedIn(endpoint.Host, endpoint.Port);

            session = new Session();
            Volatile.Write(ref _session, session);
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var readLoop = ReadLoopAsync(reader, endpoint, sessionCts.Token);
            var writeLoop = WriteLoopAsync(stream, session, sessionCts.Token);
            var keepAliveLoop = KeepAliveLoopAsync(session, sessionCts.Token);

            // Whichever loop ends first ends the connection: the board closed it, a read or write failed, or the
            // board went silent.
            var first = await Task.WhenAny(readLoop, writeLoop).ConfigureAwait(false);
            await sessionCts.CancelAsync().ConfigureAwait(false);
            session.Close();
            await IgnoreFailureAsync(readLoop).ConfigureAwait(false);
            await IgnoreFailureAsync(writeLoop).ConfigureAwait(false);
            await IgnoreFailureAsync(keepAliveLoop).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                await SayGoodbyeAsync(stream).ConfigureAwait(false);
                Trace(Vm201TraceKind.ConnectionLost, detail: StoppedByApp);
            }
            else
            {
                // Rethrows the loop's failure for the log below; a clean close by the board just returns.
                await first.ConfigureAwait(false);
                _logger.ClosedByBoard();
                Trace(Vm201TraceKind.ConnectionLost, detail: "The board closed the connection.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down, or SetEndpoint or SetLogin moving the connection.
            Trace(Vm201TraceKind.ConnectionLost, detail: StoppedByApp);
        }
        catch (Vm201AccessDeniedException)
        {
            _logger.AccessDenied();
            Trace(Vm201TraceKind.ConnectionLost, detail: "The board rejected the user name or password.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or OperationCanceledException)
        {
            _logger.ConnectionLost(ex.Message);
            Trace(Vm201TraceKind.ConnectionLost, detail: ex.Message);
        }
#pragma warning disable CA1031 // The reconnect loop must survive anything a connection throws.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.UnexpectedError(ex);
            Trace(Vm201TraceKind.ConnectionLost, detail: ex.Message);
        }
        finally
        {
            session?.Close();
            Volatile.Write(ref _session, null);
            SetDisconnected();
        }

        return loggedIn;
    }

    private async Task LoginAsync(Stream stream, Vm201PacketReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var packet = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            Received(packet);
            switch (packet.Command)
            {
                case Vm201Command.LoggedIn:
                    return;
                case Vm201Command.Auth:
                    _logger.LoginRequired();
                    var login = Login;
                    await SendAsync(
                        stream,
                        Vm201PacketCodec.Encode(Vm201Command.Username, Vm201PacketCodec.EncodeCredential(login.Username)),
                        cancellationToken).ConfigureAwait(false);
                    await SendAsync(
                        stream,
                        Vm201PacketCodec.Encode(Vm201Command.Password, Vm201PacketCodec.EncodeCredential(login.Password)),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case Vm201Command.AccessDenied:
                    throw new Vm201AccessDeniedException();
                case Vm201Command.Closed:
                    throw new IOException("The VM201 closed the connection during login.");
                default:
                    _logger.IgnoredPacket((char)packet.Command);
                    break;
            }
        }
    }

    /// <summary>
    /// Reads until the board closes the session. Fails if nothing arrives within the idle timeout, which also catches
    /// a half-open connection where the socket never reports an error.
    /// </summary>
    private async Task ReadLoopAsync(Vm201PacketReader reader, BoardEndpoint endpoint, CancellationToken cancellationToken)
    {
        while (true)
        {
            var packet = await WithTimeoutAsync(
                token => reader.ReadAsync(token).AsTask(),
                _idleTimeout,
                "receive anything",
                cancellationToken).ConfigureAwait(false);
            Received(packet);

            switch (packet.Command)
            {
                case Vm201Command.Status:
                    ApplyStatus(packet, endpoint);
                    break;
                case Vm201Command.Name:
                    // The board's channel names may contain firmware garbage; the app keeps its own names.
                    break;
                case Vm201Command.Closed:
                    return;
                default:
                    _logger.IgnoredPacket((char)packet.Command);
                    break;
            }
        }
    }

    private async Task WriteLoopAsync(Stream stream, Session session, CancellationToken cancellationToken)
    {
        await foreach (var write in session.Commands.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await SendAsync(stream, write.Packet, cancellationToken).ConfigureAwait(false);
                write.Completion?.TrySetResult();
            }
            catch (Exception ex)
            {
                write.Completion?.TrySetException(
                    new BoardNotConnectedException("The VM201 connection failed; the command was not sent.", ex));
                throw;
            }
        }
    }

    private async Task KeepAliveLoopAsync(Session session, CancellationToken cancellationToken)
    {
        var request = Vm201PacketCodec.Encode(Vm201Command.StatusRequest);
        while (true)
        {
            await Task.Delay(_keepAlive, _timeProvider, cancellationToken).ConfigureAwait(false);

            // Nobody waits for a keep-alive; if the queue is full or closed the idle timeout decides.
            session.Commands.Writer.TryWrite(new PendingWrite(request, null));
        }
    }

    // Every packet to the board goes through here, so Vm201:LogPackets sees all of them.
    private async Task SendAsync(Stream stream, byte[] packet, CancellationToken cancellationToken)
    {
        if (_options.LogPackets && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.PacketSent(Vm201PacketTrace.Describe(packet));
        }

        await stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        if (Traced is not null)
        {
            Trace(Vm201TraceKind.Sent, Vm201TraceEntry.Redact(packet));
        }
    }

    private void Received(Vm201Packet packet)
    {
        if (_options.LogPackets && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.PacketReceived(Vm201PacketTrace.Describe(Vm201PacketCodec.Encode(packet.Command, packet.Data.Span)));
        }

        if (Traced is not null)
        {
            var frame = Vm201PacketCodec.Encode(packet.Command, packet.Data.Span);
            Trace(Vm201TraceKind.Received, Vm201TraceEntry.Redact(frame));
        }
    }

    private void Trace(Vm201TraceKind kind, ReadOnlyMemory<byte> frame = default, string? detail = null)
    {
        var handlers = Traced;
        if (handlers is null)
        {
            return;
        }

        var entry = new Vm201TraceEntry(_timeProvider.GetUtcNow(), kind, frame, detail);

        // As for StateChanged: one failing subscriber can't break the others or the connection.
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<Vm201TraceEntry>>())
        {
            try
            {
                handler(this, entry);
            }
#pragma warning disable CA1031 // A subscriber's bug must not take down the connection.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.SubscriberFailed(ex);
            }
        }
    }

    // A status from an address SetEndpoint has moved away from is dropped.
    private void ApplyStatus(Vm201Packet packet, BoardEndpoint from)
    {
        if (packet.Data.Length != Vm201Command.StatusDataLength)
        {
            _logger.InvalidStatus(packet.Data.Length);
            return;
        }

        var data = packet.Data.Span;
        SetState(new Vm201State(true, data[0], data[1], (data[2] & 1) != 0, _timeProvider.GetUtcNow()), from);
    }

    private void SetDisconnected()
    {
        lock (_stateLock)
        {
            var state = State;
            if (state.IsConnected)
            {
                SetState(state with { IsConnected = false });
            }
        }
    }

    // With from set, only while the client is still at that address.
    private void SetState(Vm201State state, BoardEndpoint? from = null)
    {
        lock (_stateLock)
        {
            if (from is not null && !Equals(from, Endpoint))
            {
                return;
            }

            Volatile.Write(ref _state, state);
            RaiseStateChanged(state);
        }
    }

    private void RaiseStateChanged(Vm201State state)
    {
        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        // Call each handler on its own so one failing subscriber cannot break the others or the connection.
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<Vm201State>>())
        {
            try
            {
                handler(this, state);
            }
#pragma warning disable CA1031 // A subscriber's bug must not take down the connection.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.SubscriberFailed(ex);
            }
        }
    }

    private async Task<T> WithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        string what,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            return await operation(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The VM201 did not {what} within {timeout.TotalSeconds} s.");
        }
    }

    private async Task WithTimeoutAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan timeout,
        string what,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await operation(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The VM201 did not {what} within {timeout.TotalSeconds} s.");
        }
    }

    private async Task SayGoodbyeAsync(Stream stream)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await SendAsync(stream, Vm201PacketCodec.Encode(Vm201Command.Closed), cts.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best effort on shutdown; the socket is closed right after.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The first failure is reported by the caller; the others are consequences of it.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private sealed record PendingWrite(byte[] Packet, TaskCompletionSource? Completion);

    private sealed class Session
    {
        public Channel<PendingWrite> Commands { get; } = Channel.CreateBounded<PendingWrite>(
            new BoundedChannelOptions(CommandQueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

        /// <summary>Stops new commands and fails every command that was not written.</summary>
        public void Close()
        {
            Commands.Writer.TryComplete();
            while (Commands.Reader.TryRead(out var write))
            {
                write.Completion?.TrySetException(
                    new BoardNotConnectedException("The VM201 connection was lost; the command was not sent."));
            }
        }
    }
}
