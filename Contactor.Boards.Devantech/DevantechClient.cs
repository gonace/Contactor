using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Contactor.Boards.Devantech;

/// <summary>
/// Talks to a Devantech ETH relay board with its binary TCP commands (<see cref="DevantechApi"/>): polls its relays
/// (0x24) and inputs (0x25) through <see cref="BoardPoller{TReading}"/> and switches relays one at a time (0x20, 0x21).
/// One TCP connection and one command at a time (the poller sees to it), each command in one write, each waiting for
/// its answer of a fixed length within <see cref="RequestTimeout"/>. Every new connection first checks that the board
/// is the chosen model (0x10) and unlocks it with the password when it is locked (0x7A, 0x79); a command checks the lock
/// again, since it comes back after 30 s without a command. A request without an answer or with a value its answer
/// can't have closes the connection, and the next request opens a new one. Commands are never queued: without a
/// connection they fail at once with <see cref="BoardNotConnectedException"/>, a poll under way gives way to them, and
/// the board's state right after them is reported at once. The password is never logged or traced.
/// </summary>
public sealed class DevantechClient : IPolledBoard<DevantechStatus>, IDisposable
{
    /// <summary>How long a request may take before it counts as unanswered; also the limit to connect.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    // Bytes waiting before a request were sent unasked; more than this and the connection is opened afresh.
    private const int MaxSkippedBytes = 4096;

    private readonly BoardModel _model;
    private readonly DevantechLayout _layout;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DevantechClient> _logger;
    private readonly BoardPoller<DevantechStatus> _poller;

    private BoardEndpoint _endpoint;
    private string _password;

    // The connection; only used under the poller's lock (a connect, a poll or a command), and by Dispose.
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private DevantechStatus? _lastTraced;
    private int _wasConnected;

    public DevantechClient(IOptions<DevantechOptions> options, BoardModel model, TimeProvider timeProvider, ILogger<DevantechClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        if (!DevantechModels.IsDevantech(model))
        {
            throw new ArgumentException($"{model.Name} isn't a Devantech board.", nameof(model));
        }

        var settings = options.Value;
        if (settings.PollSeconds < 1)
        {
            throw new ArgumentException("Devantech:PollSeconds must be at least 1.", nameof(options));
        }

        // Fail at start, not on the first connection, if the password can't be sent.
        var password = settings.Password ?? "";
        if (DevantechModels.CheckPassword(password) is { } error)
        {
            throw new ArgumentException($"Devantech:Password can't be sent to the board: {error}", nameof(options));
        }

        _model = model;
        _layout = DevantechModels.Layout(model);
        _password = password;
        _endpoint = new BoardEndpoint(settings.Host, settings.Port);
        _timeProvider = timeProvider;
        _logger = logger;
        _poller = new BoardPoller<DevantechStatus>(this, timeProvider)
        {
            PollInterval = TimeSpan.FromSeconds(settings.PollSeconds),
        };
    }

    /// <summary>
    /// Raised when the relays or the inputs change, and when the connection is lost or comes back; not on a poll that
    /// changed nothing. On the poller's loop: handlers must be quick.
    /// </summary>
    public event EventHandler<DevantechState>? StateChanged;

    /// <summary>Every command and answer and every connection step, in plain words. Handlers must be quick.</summary>
    public event EventHandler<BoardTraceEntry>? Traced;

    /// <summary>The model it talks to: how many relays and inputs to read, and which module id to expect.</summary>
    public BoardModel Model => _model;

    /// <summary>The latest state; refreshed on every poll, so its last contact is always current.</summary>
    public DevantechState State
    {
        get
        {
            var state = _poller.State;
            return new DevantechState(state.IsConnected, state.Reading, state.LastContactUtc);
        }
    }

    /// <summary>Where the client connects: <c>Devantech:Host</c>/<c>Port</c> until <see cref="SetEndpoint"/> moves it.</summary>
    public BoardEndpoint Endpoint => Volatile.Read(ref _endpoint);

    /// <summary>The TCP password it unlocks the board with; empty for none. A secret: never log it.</summary>
    public string Password => Volatile.Read(ref _password);

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

    /// <summary>
    /// Connects again at once with another password, empty for none; nothing happens if it is the same. Throws
    /// <see cref="ArgumentException"/> for one the board can't take (<see cref="DevantechModels.CheckPassword"/>).
    /// </summary>
    public void SetPassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (DevantechModels.CheckPassword(password) is { } error)
        {
            throw new ArgumentException(error, nameof(password));
        }

        if (Interlocked.Exchange(ref _password, password) == password)
        {
            return;
        }

        _logger.PasswordChanged(password.Length == 0 ? "none" : "set");
        _poller.Restart();
    }

    /// <summary>Polls until cancelled. Never throws because of the board. Call it once.</summary>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        return _poller.RunAsync(cancellationToken);
    }

    /// <summary>Turns on the relays in <paramref name="mask"/> (bit 0 = relay 1), one command each.</summary>
    public Task TurnOnAsync(ulong mask, CancellationToken cancellationToken = default)
    {
        return CommandAsync(mask, turnOn: true, cancellationToken);
    }

    /// <summary>Turns off the relays in <paramref name="mask"/> (bit 0 = relay 1), one command each.</summary>
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

    Task IPolledBoard<DevantechStatus>.ConnectAsync(CancellationToken cancellationToken)
    {
        return OpenAsync(cancellationToken);
    }

    Task<DevantechStatus> IPolledBoard<DevantechStatus>.ReadAsync(CancellationToken cancellationToken)
    {
        return ReadStatusAsync(cancellationToken);
    }

    void IPolledBoard<DevantechStatus>.OnStateChanged(PolledState<DevantechStatus> state)
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

        var devantechState = new DevantechState(state.IsConnected, state.Reading, state.LastContactUtc);

        // Each subscriber on its own: one that fails must not keep the state from the others.
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<DevantechState>>())
        {
            try
            {
                handler(this, devantechState);
            }
#pragma warning disable CA1031 // A subscriber's bug must not take down the poller or the other subscribers.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.SubscriberFailed(ex);
            }
        }
    }

    void IPolledBoard<DevantechStatus>.OnStep(BoardPollStep kind, string? detail)
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

        // A poll under way gives way. The board switches one relay per command, and the whole mask goes out, as one
        // command on a board that takes a mask would: a group's interlock and "All off" rely on every OFF being sent.
        // Only a command that gets no answer at all (the board is gone) stops it.
        try
        {
            await _poller.RunCommandAsync(token => SwitchEachAsync(mask, turnOn, token), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNoAnswer(ex))
        {
            // The poller counted it as a failed poll and reads the board at once, which shows what it did.
            throw new BoardNotConnectedException("The Devantech board didn't answer; the command may not have been carried out.", ex);
        }
        catch (Exception ex) when (IsNotTaken(ex))
        {
            // No relay was taken, or the board couldn't be unlocked: counted and read in the same way.
            throw new BoardNotConnectedException($"The Devantech board didn't take the command: {ex.Message}", ex);
        }
    }

    // Under the poller's lock. Returns the board's state after the switches, which the poller takes as a poll's.
    private async Task<DevantechStatus> SwitchEachAsync(ulong mask, bool turnOn, CancellationToken cancellationToken)
    {
        // The board locks again after 30 s without a command, so a command checks first. A board it can't unlock
        // switches nothing: that throws before any relay is sent.
        await UnlockAsync(cancellationToken).ConfigureAwait(false);

        ulong taken = 0;
        string? refusal = null;
        foreach (var relay in new ChannelMask(mask).Channels)
        {
            // No answer (IsNoAnswer) is thrown as it is and stops the rest: the poller counts it as a failed poll.
            try
            {
                var request = DevantechProtocol.Switch(relay, turnOn);
                var answer = (await ExchangeAsync(request, routine: false, cancellationToken).ConfigureAwait(false))[0];
                if (answer is not (DevantechApi.Success or DevantechApi.Failure))
                {
                    throw Unreadable(string.Create(CultureInfo.InvariantCulture, $"The board answered {answer:X2} to a relay command."), [answer]);
                }

                Trace(BoardTraceDirection.Received, DevantechTraceText.Switched(request.Bytes, answer), DevantechProtocol.Hex([answer]), isError: answer != DevantechApi.Success);
                if (answer != DevantechApi.Success)
                {
                    throw new DevantechRefusedException(string.Create(CultureInfo.InvariantCulture, $"The board refused relay {relay}."));
                }

                taken |= 1UL << (relay - 1);
            }
            catch (Exception ex) when (IsNotTaken(ex))
            {
                // Refused, or an answer that can't be read: the relay may or may not have switched. The others still
                // go (an unreadable answer closed the connection; the next command opens and unlocks a new one); the
                // read after them shows the truth.
                refusal ??= ex.Message;
                Trace(
                    BoardTraceDirection.None,
                    string.Create(CultureInfo.InvariantCulture, $"The board didn't take the command for relay {relay}: {ex.Message}"),
                    isError: true);
            }
        }

        // Not one relay was taken: the command failed, as on a board that takes the mask at once, and nothing is
        // counted as switched. The poller counts it as a failed poll and reads the board at once.
        if (taken == 0)
        {
            throw new DevantechRefusedException(refusal!);
        }

        try
        {
            return await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNotTaken(ex))
        {
            // Never a state the board didn't read: a group would take a made-up "on" as confirmation. The last state
            // read stays, and a read at once shows the truth. The command doesn't fail (what was taken was sent), so
            // the poller's read after a failed command doesn't come: ask for it here.
            if (_poller.State.Reading is not { } last)
            {
                throw;
            }

            _poller.PollNow();
            return last;
        }
    }

    private async Task<DevantechStatus> ReadStatusAsync(CancellationToken cancellationToken)
    {
        var last = Volatile.Read(ref _lastTraced);
        var relayCount = _model.Capabilities.RelayCount;
        var outputs = await ExchangeAsync(DevantechProtocol.GetOutputs(_layout), routine: true, cancellationToken).ConfigureAwait(false);
        var relays = DevantechProtocol.Outputs(outputs, relayCount);
        Trace(BoardTraceDirection.Received, DevantechTraceText.Relays(relays, relayCount), DevantechProtocol.Hex(outputs), isRoutine: last?.Relays == relays);

        ulong inputs = 0;
        var inputCount = _model.Capabilities.InputCount;
        if (inputCount > 0)
        {
            var answer = await ExchangeAsync(DevantechProtocol.GetInputs(_layout), routine: true, cancellationToken).ConfigureAwait(false);
            inputs = DevantechProtocol.Inputs(answer, inputCount);
            Trace(BoardTraceDirection.Received, DevantechTraceText.Inputs(inputs, inputCount), DevantechProtocol.Hex(answer), isRoutine: last?.Inputs == inputs);
        }

        var status = new DevantechStatus(relays, inputs);
        Volatile.Write(ref _lastTraced, status);
        return status;
    }

    // Asks whether the board is locked, and sends the password when it is. Throws DevantechAccessDeniedException when
    // the board refuses the password, or asks for one and there is none.
    private async Task UnlockAsync(CancellationToken cancellationToken)
    {
        var answer = (await ExchangeAsync(DevantechProtocol.GetUnlockTime(), routine: false, cancellationToken).ConfigureAwait(false))[0];
        if (answer is > DevantechApi.UnlockSeconds and not DevantechApi.NoPassword)
        {
            throw Unreadable(string.Create(CultureInfo.InvariantCulture, $"The board answered {answer:X2} to the lock check."), [answer]);
        }

        Trace(BoardTraceDirection.Received, DevantechTraceText.UnlockTime(answer), DevantechProtocol.Hex([answer]));
        if (answer != DevantechApi.Locked)
        {
            return;
        }

        var password = Password;
        if (password.Length == 0)
        {
            const string message = "The Devantech board asks for a password. Enter it on the Settings screen.";
            Trace(BoardTraceDirection.None, message, isError: true);
            throw new DevantechAccessDeniedException(message);
        }

        var result = (await ExchangeAsync(DevantechProtocol.Password(password), routine: false, cancellationToken).ConfigureAwait(false))[0];
        if (result is not (DevantechApi.PasswordAccepted or DevantechApi.PasswordRefused))
        {
            throw Unreadable(string.Create(CultureInfo.InvariantCulture, $"The board answered {result:X2} to the password."), [result]);
        }

        Trace(BoardTraceDirection.Received, DevantechTraceText.PasswordAnswer(result), DevantechProtocol.Hex([result]), isError: result != DevantechApi.PasswordAccepted);
        if (result != DevantechApi.PasswordAccepted)
        {
            throw new DevantechAccessDeniedException();
        }
    }

    // A command that got no answer at all: it counts as a failed poll, whether a poll or a command sent it.
    private static bool IsNoAnswer(Exception exception)
    {
        return exception is TimeoutException or IOException or SocketException;
    }

    // The board answered, but didn't do it, couldn't be read, couldn't be unlocked or isn't the model.
    private static bool IsNotTaken(Exception exception)
    {
        return exception is DevantechRefusedException or DevantechProtocolException or DevantechAccessDeniedException or DevantechWrongModelException;
    }

    /// <summary>
    /// Sends one command and reads its answer, within <see cref="RequestTimeout"/>, on the open connection or a new one.
    /// Traces the command and anything but an answer, which the caller traces as it knows what it says. Closes the
    /// connection when the answer doesn't come.
    /// </summary>
    private async Task<byte[]> ExchangeAsync(DevantechRequest request, bool routine, CancellationToken cancellationToken)
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
        Trace(BoardTraceDirection.Sent, DevantechTraceText.Request(request.Bytes), DevantechTraceText.RequestBytes(request.Bytes), isRoutine: routine);
        using var timeout = new CancellationTokenSource(RequestTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            // All bytes of a command in one write, and so one packet: the board takes a command split in two as two.
            await stream.WriteAsync(request.Bytes, linked.Token).ConfigureAwait(false);
            return await DevantechProtocol.ReadAnswerAsync(stream, request.AnswerLength, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // A command the board doesn't know gets no answer at all, so this is also what a wrong model looks like.
            Close();
            var message = $"The Devantech board did not answer within {RequestTimeout.TotalSeconds} s.";
            Trace(BoardTraceDirection.None, $"No answer: {message}", isError: true);
            throw new TimeoutException(message);
        }
        catch (OperationCanceledException)
        {
            // A command took the poll's place, or the app stops: the answer may still come, out of step.
            Close();
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

    // Opens a new connection to the board, closing any before; then checks the model and unlocks the board. Throws
    // TimeoutException, SocketException or IOException when the board doesn't answer, DevantechWrongModelException for
    // another model and DevantechAccessDeniedException for a password it doesn't take.
    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        Close();
        var endpoint = Endpoint;
        var tcp = new TcpClient { NoDelay = true };
        using (var timeout = new CancellationTokenSource(RequestTimeout, _timeProvider))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
        {
            try
            {
                await tcp.ConnectAsync(endpoint.Host, endpoint.Port, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                tcp.Dispose();
                throw new TimeoutException($"The Devantech board did not take a connection within {RequestTimeout.TotalSeconds} s.");
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }

        _tcp = tcp;
        _stream = tcp.GetStream();

        // Another model's relays are numbered and laid out otherwise: nothing is switched or read on it.
        var info = await ExchangeAsync(DevantechProtocol.GetModuleInfo(), routine: false, cancellationToken).ConfigureAwait(false);
        Trace(BoardTraceDirection.Received, DevantechTraceText.Module(info), DevantechProtocol.Hex(info));
        if (DevantechModels.CheckModule(_model, info[0]) is { } wrongModel)
        {
            Close();
            Trace(BoardTraceDirection.None, wrongModel, isError: true);
            throw new DevantechWrongModelException(wrongModel);
        }

        await UnlockAsync(cancellationToken).ConfigureAwait(false);
    }

    // The answer has a value it can't have: the connection is closed, since what comes next can't be trusted.
    private DevantechProtocolException Unreadable(string message, byte[] answer)
    {
        Close();
        Trace(BoardTraceDirection.Received, $"Unreadable answer: {message}", DevantechProtocol.Hex(answer), isError: true);
        return new DevantechProtocolException(message, answer);
    }

    // Nothing comes unasked from the board; bytes waiting now are left from an answer that came too late or was too
    // long. They are dropped, or with too many of them the connection is opened afresh at the next request.
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
            DevantechProtocol.Hex(skipped.AsSpan(0, read)),
            isError: true);
    }

    private void Close()
    {
        var tcp = Interlocked.Exchange(ref _tcp, null);
        _stream = null;
        tcp?.Dispose();
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
