using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Contactor.Boards.Devantech;

/// <summary>
/// Talks to a Devantech ETH relay board with its binary TCP commands (<see cref="DevantechApi"/>): polls its relays
/// (0x24), inputs (0x25) and analog inputs (0x32 per input, or 0x34 for all on the ETH1610) through
/// <see cref="BoardPoller{TReading}"/> and switches relays one at a time (0x20, 0x21). The analog inputs are read in
/// polls only, before the relays and inputs, and an analog answer that fails never fails the poll: it gives
/// "no reading" for a while (<see cref="AnalogRetryInterval"/>). One TCP connection and one command at a time (the
/// poller sees to it), each command in one write, each waiting for its answer of a fixed length within
/// <see cref="RequestTimeout"/>. Every new connection first checks that the board
/// is the chosen model (0x10) and unlocks it with the password when it is locked (0x7A, 0x79); a command checks the lock
/// again, since it comes back after 30 s without a command. A request without an answer or with a value its answer
/// can't have closes the connection, and the next request opens a new one. A request on a connection from before that
/// turns out broken (the board may close one it found idle) goes once more on a new one. Commands are never queued:
/// without a connection they fail at once with <see cref="BoardNotConnectedException"/>, a poll under way gives way to
/// them, and the board's state right after them is reported at once. The password is never logged or traced.
/// </summary>
public sealed class DevantechClient : IPolledBoard<DevantechStatus>, IDisposable, IAsyncDisposable
{
    /// <summary>How long a request may take before it counts as unanswered; also the limit to connect.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the client leaves the analog inputs alone after their answer failed: a board that never answers them
    /// costs one time-out per this, not one per poll. A new connection from the poller tries at once.
    /// </summary>
    public static readonly TimeSpan AnalogRetryInterval = TimeSpan.FromMinutes(1);

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

    // Only used under the poller's lock (a connect, a poll or a command). What the last poll read of the analog inputs,
    // which a command's read takes over, and until when they are left alone after a failed answer.
    private AnalogReadings? _lastAnalog;
    private DateTimeOffset? _analogRetryAt;
    private int _wasConnected;

    // Set by Dispose: no connection is opened after it, also by a request still under way.
    private int _disposed;

    // True while a new connection is checked and unlocked: its requests don't go once more on yet another one.
    private bool _checking;

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
        _poller = new BoardPoller<DevantechStatus>(this, timeProvider, DevantechStatus.SwitchesComparer)
        {
            PollInterval = TimeSpan.FromSeconds(settings.PollSeconds),
        };
    }

    /// <summary>
    /// Raised when the relays or the inputs change, and when the connection is lost or comes back; not on a poll that
    /// changed nothing. On the poller's loop: handlers must be quick.
    /// </summary>
    public event EventHandler<DevantechState>? StateChanged;

    /// <summary>
    /// Raised when a poll or command finds only the analog values changed: the relays, the inputs and the connection
    /// are as before, so <see cref="StateChanged"/> isn't raised. On the poller's loop: handlers must be quick.
    /// </summary>
    public event EventHandler<DevantechState>? AnalogChanged;

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
            return new DevantechState(state.IsConnected, state.Reading, state.LastContactUtc, state.Problem);
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
            throw new ArgumentException(error.ToString(), nameof(password));
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
    public Task<ulong> TurnOnAsync(ulong mask, CancellationToken cancellationToken = default)
    {
        return CommandAsync(mask, relay => DevantechProtocol.Switch(relay, on: true), cancellationToken);
    }

    /// <summary>Turns off the relays in <paramref name="mask"/> (bit 0 = relay 1), one command each.</summary>
    public Task<ulong> TurnOffAsync(ulong mask, CancellationToken cancellationToken = default)
    {
        return CommandAsync(mask, relay => DevantechProtocol.Switch(relay, on: false), cancellationToken);
    }

    /// <summary>
    /// Pulses the relays in <paramref name="mask"/> (bit 0 = relay 1), one command each (0x20 with a pulse time), as
    /// <see cref="TurnOnAsync"/>: the board turns each on, and off again by itself after <paramref name="length"/>.
    /// Throws <see cref="ArgumentOutOfRangeException"/>, before anything is sent, for a length
    /// <see cref="DevantechModels.Pulse"/> can't do.
    /// </summary>
    public Task<ulong> PulseAsync(ulong mask, TimeSpan length, CancellationToken cancellationToken = default)
    {
        if (!DevantechModels.Pulse.CanDo(length))
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "A Devantech board pulses for 0.1 s to 25.5 s, in steps of 0.1 s.");
        }

        return CommandAsync(mask, relay => DevantechProtocol.Pulse(relay, length), cancellationToken);
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

    Task IPolledBoard<DevantechStatus>.ConnectAsync(CancellationToken cancellationToken)
    {
        // A new connection, maybe to another board: the analog inputs are read again at once.
        _lastAnalog = null;
        _analogRetryAt = null;
        return OpenAsync(cancellationToken);
    }

    Task<DevantechStatus> IPolledBoard<DevantechStatus>.ReadAsync(CancellationToken cancellationToken)
    {
        return ReadStatusAsync(readAnalog: true, cancellationToken);
    }

    void IPolledBoard<DevantechStatus>.OnStateChanged(PolledState<DevantechStatus> state)
    {
        var wasConnected = Interlocked.Exchange(ref _wasConnected, state.IsConnected ? 1 : 0) == 1;
        if (state.IsConnected && !wasConnected)
        {
            var endpoint = Endpoint;
            _logger.Connected(endpoint.Host, endpoint.Port);
        }

        Raise(StateChanged, state);
    }

    void IPolledBoard<DevantechStatus>.OnReadingChanged(PolledState<DevantechStatus> state)
    {
        // The comparer leaves out only the analog values: that is what changed.
        Raise(AnalogChanged, state);
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

    // Sends request(relay) for every relay in the mask: an ON, an OFF or a pulse.
    private async Task<ulong> CommandAsync(ulong mask, Func<int, DevantechRequest> request, CancellationToken cancellationToken)
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

        // A poll under way gives way. The board switches one relay per command, and the whole mask goes out, as one
        // command on a board that takes a mask would: a group's interlock and "All off" rely on every OFF being sent.
        // Only a command that gets no answer at all, also on a new connection (the board is gone), stops it.
        try
        {
            var taken = new StrongBox<ulong>();
            await _poller.RunSwitchesAsync(token => SwitchEachAsync(mask, request, taken, token), cancellationToken).ConfigureAwait(false);
            return taken.Value;
        }
        catch (Exception ex) when (IsNoAnswer(ex))
        {
            // The poller counted it as a failed poll and reads the board at once, which shows what it did.
            throw new BoardNotConnectedException("The Devantech board didn't answer; the command may not have been carried out.", ex);
        }
        catch (Exception ex) when (IsNotTaken(ex) || ex is BoardRefusedException)
        {
            // No relay was taken, or the board couldn't be unlocked. The poller read the board at once, and counted it
            // as a failed poll unless the board refused every relay (BoardRefusedException): that is an answer.
            throw new BoardNotConnectedException($"The Devantech board didn't take the command: {ex.Message}", ex);
        }
    }

    // Under the poller's lock. Returns the board's state read after the switches, which the poller takes as a poll's,
    // or none when that read failed, and puts the relays the board took in <paramref name="taken"/>.
    private async Task<CommandReading<DevantechStatus>> SwitchEachAsync(ulong mask, Func<int, DevantechRequest> requestFor, StrongBox<ulong> taken, CancellationToken cancellationToken)
    {
        // The board locks again after 30 s without a command, so a command checks first. A board it can't unlock
        // switches nothing: that throws before any relay is sent.
        await UnlockAsync(cancellationToken).ConfigureAwait(false);

        string? refusal = null;
        var unreadable = false;
        foreach (var relay in new ChannelMask(mask).Channels)
        {
            // No answer (IsNoAnswer), also on the new connection ExchangeAsync tries after a broken one, is thrown as it
            // is and stops the rest: the poller counts it as a failed poll.
            try
            {
                var request = requestFor(relay);
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

                taken.Value |= 1UL << (relay - 1);
            }
            catch (Exception ex) when (IsNotTaken(ex))
            {
                // Refused, or an answer that can't be read: the relay may or may not have switched. The others still
                // go (an unreadable answer closed the connection; the next command opens and unlocks a new one); the
                // read after them shows the truth.
                refusal ??= ex.Message;
                unreadable |= ex is not DevantechRefusedException;
                Trace(
                    BoardTraceDirection.None,
                    string.Create(CultureInfo.InvariantCulture, $"The board didn't take the command for relay {relay}: {ex.Message}"),
                    isError: true);
            }
        }

        // Not one relay was taken: the command failed, as on a board that takes the mask at once, and nothing is
        // counted as switched. The poller reads the board at once. When the board answered 01 to every relay, it
        // answered: that doesn't count toward losing it. Anything else (an unreadable answer, a new connection it
        // couldn't check or unlock) counts as a failed poll.
        if (taken.Value == 0)
        {
            throw unreadable ? new DevantechRefusedException(refusal!) : new BoardRefusedException(refusal!);
        }

        try
        {
            return new CommandReading<DevantechStatus>(await ReadStatusAsync(readAnalog: false, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (IsNotTaken(ex) || IsNoAnswer(ex))
        {
            // Never a state the board didn't read: a group would take a made-up "on" as confirmation. The command
            // doesn't fail (what was taken was sent), and it isn't an answered poll: the poller keeps the last state
            // read and reads the board at once, which counts as a poll of its own if it fails too.
            return default;
        }
    }

    // Only in a poll (readAnalog), the analog inputs first, so a command's confirmation never waits on them: a command's
    // read keeps what the last poll read of them. Then the relays and inputs, last of all, so the relays the poll reports
    // are as fresh as they can be, however long the analog inputs took.
    private async Task<DevantechStatus> ReadStatusAsync(bool readAnalog, CancellationToken cancellationToken)
    {
        var last = Volatile.Read(ref _lastTraced);
        string? analogProblem = null;
        if (readAnalog && _model.Capabilities.Analog is { } ability)
        {
            (_lastAnalog, analogProblem) = await ReadAnalogAsync(ability, last?.Analog, cancellationToken).ConfigureAwait(false);
        }

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

        // Only once the relays and inputs answered: a board that has just died fails them too, and that is no reason
        // to blame the analog inputs or leave them alone for a minute.
        if (analogProblem is not null)
        {
            _analogRetryAt = _timeProvider.GetUtcNow() + AnalogRetryInterval;
            _logger.AnalogNotRead(analogProblem);
            Trace(
                BoardTraceDirection.None,
                string.Create(CultureInfo.InvariantCulture, $"No analog readings: {analogProblem} The relays and inputs are read as usual; the analog inputs are asked for again in {AnalogRetryInterval.TotalMinutes} min."),
                isError: true);
        }

        var status = new DevantechStatus(relays, inputs, _lastAnalog);
        Volatile.Write(ref _lastTraced, status);
        return status;
    }

    // The analog inputs, or "no reading" for each when their answer is late, short or can't be read, or while they are
    // left alone after such an answer (AnalogRetryInterval). That never fails the poll: an unproven measurement must not
    // take away the relays. A late or short answer closed the connection (ExchangeAsync), so its bytes can't be taken
    // for the next answer: the relays and inputs after it go on a new one, in the same poll. A board that can't be
    // reached, checked or unlocked is opened for before the first analog request, so it fails the poll as it would for
    // the relays, and isn't taken for a problem with the analog inputs. A cancellation (a command takes the poll's
    // place) fails the poll too, as for any request. Returns why the answer failed as well, for ReadStatusAsync to start
    // the wait once the relays and inputs answered; null when it didn't fail.
    private async Task<(AnalogReadings Readings, string? Problem)> ReadAnalogAsync(AnalogAbility ability, AnalogReadings? last, CancellationToken cancellationToken)
    {
        var none = new AnalogReadings(new int?[ability.Count]);
        if (_analogRetryAt is { } retryAt && _timeProvider.GetUtcNow() < retryAt)
        {
            return (none, null);
        }

        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        _analogRetryAt = null;
        AnalogReadings? readings;
        string? problem;
        try
        {
            (readings, problem) = await TryReadAnalogAsync(ability, last, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNoAnswer(ex))
        {
            (readings, problem) = (null, ex.Message);
        }

        return readings is not null ? (readings, null) : (none, problem ?? "");
    }

    // One 0x34 for all inputs on the ETH1610, one 0x32 per input on the others. Returns the readings, or why the answer
    // can't be read: a value above 1023, or for 0x34 a length other than 2 bytes per input. Throws as ExchangeAsync.
    private async Task<(AnalogReadings? Readings, string? Problem)> TryReadAnalogAsync(AnalogAbility ability, AnalogReadings? last, CancellationToken cancellationToken)
    {
        if (_layout.AllAnalog)
        {
            var answer = await ExchangeAsync(DevantechProtocol.GetAllAnalog(ability.Count), routine: true, cancellationToken).ConfigureAwait(false);
            if (DevantechProtocol.AnalogValues(answer, ability.Count) is not { } values)
            {
                var expected = ability.Count * DevantechProtocol.AnalogValueBytes;
                var message = answer.Length != expected
                    ? string.Create(CultureInfo.InvariantCulture, $"The board answered {answer.Length} bytes to the analog inputs, not {expected}.")
                    : "The board answered a value above 1023 to the analog inputs.";
                return (null, UnreadableAnalog(message, answer));
            }

            var readings = new AnalogReadings(values.Select(v => (int?)v));
            Trace(BoardTraceDirection.Received, DevantechTraceText.AllAnalog(values, ability), DevantechProtocol.Hex(answer), isRoutine: last == readings);
            return (readings, null);
        }

        var counts = new int?[ability.Count];
        for (var input = 1; input <= ability.Count; input++)
        {
            var answer = await ExchangeAsync(DevantechProtocol.GetAnalog(input), routine: true, cancellationToken).ConfigureAwait(false);
            if (DevantechProtocol.AnalogValue(answer) is not { } value)
            {
                return (null, UnreadableAnalog(string.Create(CultureInfo.InvariantCulture, $"The board answered {DevantechProtocol.Hex(answer)} to analog input {input}."), answer));
            }

            Trace(BoardTraceDirection.Received, DevantechTraceText.Analog(input, value, ability), DevantechProtocol.Hex(answer), isRoutine: last?.Input(input) == value);
            counts[input - 1] = value;
        }

        return (new AnalogReadings(counts), null);
    }

    // An analog answer of the right length with a value it can't have. The whole answer was read, so the connection is
    // still in step and stays open.
    private string UnreadableAnalog(string message, byte[] answer)
    {
        Trace(BoardTraceDirection.Received, $"Unreadable answer: {message}", DevantechProtocol.Hex(answer), isError: true);
        return message;
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
            throw new DevantechAccessDeniedException(message, new ConnectionProblem(ConnectionProblemKind.LoginNeeded, _model));
        }

        var result = (await ExchangeAsync(DevantechProtocol.Password(password), routine: false, cancellationToken).ConfigureAwait(false))[0];
        if (result is not (DevantechApi.PasswordAccepted or DevantechApi.PasswordRefused))
        {
            throw Unreadable(string.Create(CultureInfo.InvariantCulture, $"The board answered {result:X2} to the password."), [result]);
        }

        Trace(BoardTraceDirection.Received, DevantechTraceText.PasswordAnswer(result), DevantechProtocol.Hex([result]), isError: result != DevantechApi.PasswordAccepted);
        if (result != DevantechApi.PasswordAccepted)
        {
            throw new DevantechAccessDeniedException(
                "The Devantech board refused the password.",
                new ConnectionProblem(ConnectionProblemKind.LoginRefused, _model));
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
    /// connection when the answer doesn't come. When the connection was open from before and turns out broken (the
    /// board may close one it found idle) before any of the answer came, the command goes once more on a new connection,
    /// which is checked and unlocked first: every command the client sends does the same when done twice (reading,
    /// switching one relay, starting its pulse again, the password).
    /// </summary>
    private async Task<byte[]> ExchangeAsync(DevantechRequest request, bool routine, CancellationToken cancellationToken)
    {
        // Not one just opened to check and unlock it: that one goes nowhere else. One opened before this call, also just
        // before it (ReadAnalogAsync opens one first), counts as from before: its first command may go once more.
        var reused = _stream is not null && !_checking;
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            SkipUnasked();
        }
        catch (IOException ex)
        {
            Close();
            Trace(BoardTraceDirection.None, $"No answer: {ex.Message}", isError: true);
            if (reused)
            {
                // Closed above, so the command goes on a new connection, and only once more: a new one isn't reused.
                return await ExchangeAsync(request, routine, cancellationToken).ConfigureAwait(false);
            }

            throw;
        }

        var stream = _stream!;
        var reader = new CountingStream(stream);
        Trace(BoardTraceDirection.Sent, DevantechTraceText.Request(request.Bytes), DevantechTraceText.RequestBytes(request.Bytes), isRoutine: routine);
        using var timeout = new CancellationTokenSource(RequestTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            // All bytes of a command in one write, and so one packet: the board takes a command split in two as two.
            await stream.WriteAsync(request.Bytes, linked.Token).ConfigureAwait(false);
            return await DevantechProtocol.ReadAnswerAsync(reader, request.AnswerLength, linked.Token).ConfigureAwait(false);
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
            if (reused && reader.BytesRead == 0)
            {
                // Closed above, so the command goes on a new connection, and only once more: a new one isn't reused.
                return await ExchangeAsync(request, routine, cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
    }

    // Opens a new connection, checked and unlocked, unless one is open. Traces a connection the board doesn't take;
    // the check's and the unlock's own commands trace what goes wrong with them. Throws as OpenAsync.
    private async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (_stream is not null)
        {
            return;
        }

        try
        {
            await ConnectTcpAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNoAnswer(ex))
        {
            Trace(BoardTraceDirection.None, $"No answer: {ex.Message}", isError: true);
            throw;
        }

        await CheckAndUnlockAsync(cancellationToken).ConfigureAwait(false);
    }

    // Opens a new connection to the board, closing any before; then checks the model and unlocks the board. Throws
    // TimeoutException, SocketException or IOException when the board doesn't answer, DevantechWrongModelException for
    // another model and DevantechAccessDeniedException for a password it doesn't take.
    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        await ConnectTcpAsync(cancellationToken).ConfigureAwait(false);
        await CheckAndUnlockAsync(cancellationToken).ConfigureAwait(false);
    }

    // Opens a new TCP connection, closing any before. Throws TimeoutException, SocketException or IOException, also
    // once the client is disposed.
    private async Task ConnectTcpAsync(CancellationToken cancellationToken)
    {
        Close();
        ThrowIfDisposed();
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

        _stream = tcp.GetStream();
        Interlocked.Exchange(ref _tcp, tcp);

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

    // On a new connection: the module id, then the lock.
    private async Task CheckAndUnlockAsync(CancellationToken cancellationToken)
    {
        _checking = true;
        try
        {
            // Another model's relays are numbered and laid out otherwise: nothing is switched or read on it.
            var info = await ExchangeAsync(DevantechProtocol.GetModuleInfo(), routine: false, cancellationToken).ConfigureAwait(false);
            Trace(BoardTraceDirection.Received, DevantechTraceText.Module(info), DevantechProtocol.Hex(info));
            if (DevantechModels.CheckModule(_model, info[0]) is { } wrongModel)
            {
                Close();
                Trace(BoardTraceDirection.None, wrongModel, isError: true);
                throw new DevantechWrongModelException(
                    wrongModel,
                    new ConnectionProblem(ConnectionProblemKind.WrongModel, _model, DevantechModels.ModelFor(info[0])));
            }

            await UnlockAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _checking = false;
        }
    }

    // The answer has a value it can't have: the connection is closed, since what comes next can't be trusted.
    private DevantechProtocolException Unreadable(string message, byte[] answer)
    {
        Close();
        Trace(BoardTraceDirection.Received, $"Unreadable answer: {message}", DevantechProtocol.Hex(answer), isError: true);
        return new DevantechProtocolException(message, answer);
    }

    // Nothing comes unasked from the board; bytes waiting now are left from an answer that came too late or was too
    // long. They are dropped. With too many of them the connection is closed: a command on a connection from before
    // goes once more on a new one at once, and one on a new connection fails.
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

    // Each subscriber on its own: one that fails must not keep the state from the others.
    private void Raise(EventHandler<DevantechState>? handlers, PolledState<DevantechStatus> state)
    {
        if (handlers is null)
        {
            return;
        }

        var devantechState = new DevantechState(state.IsConnected, state.Reading, state.LastContactUtc, state.Problem);
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
