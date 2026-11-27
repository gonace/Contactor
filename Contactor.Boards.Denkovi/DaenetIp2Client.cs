using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Contactor.Boards.Denkovi;

/// <summary>
/// Talks to a Denkovi DAEnetIP2 v2 kit over SNMPv1 on UDP (<see cref="DaenetIp2Api"/>): polls the ports its relays are
/// on (a GetRequest of each port's byte, last in a poll) and the 8 analog inputs of P6 (a GetRequest of each) through
/// <see cref="BoardPoller{TReading}"/>, and switches relays one at a time (a SetRequest of the relay's pin). The analog
/// inputs are read in polls only, and an analog answer that fails never fails the poll: it gives "no reading" for a
/// while (<see cref="AnalogRetryInterval"/>). One request at a time (the poller sees to it), each with its own request
/// id, waiting <see cref="RequestTimeout"/> for its answer and asking once more when none comes (<see cref="Attempts"/>).
/// A datagram from another address, with another request id, or one the codec can't read is skipped. The board answers
/// a wrong community with silence, so a board that never answers may have another community: the trace says so.
/// Commands are never queued: without an answer to a poll they fail at once with <see cref="BoardNotConnectedException"/>,
/// a poll under way gives way to them, and the board's state right after them is reported at once. The community, a
/// secret, is never logged or traced: the trace shows the PDU only.
/// It also reads the board's own rules, the ICMP monitoring's restart pins (<c>cfgP5DupRST</c>), at the first poll on each
/// connection and then every <see cref="RulesInterval"/>, after the ports, once the firmware version (<c>cfgVersion</c>,
/// read once per connection) says the board has them (4.097 and later). It never writes them on its own: only
/// <see cref="TurnOffOwnRulesAsync"/>, on a person's press. Every request is for an OID of the closed list
/// (<see cref="DaenetIp2Oid"/>).
/// </summary>
public sealed class DaenetIp2Client : IPolledBoard<DaenetIp2Status>, IDisposable, IAsyncDisposable
{
    /// <summary>How long one attempt waits for its answer; also the limit to look up the board's host name.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(1);

    /// <summary>How many times a request is sent before it counts as unanswered: once, and once more.</summary>
    public const int Attempts = 2;

    /// <summary>
    /// How many relays in a row may get no answer, both tries each, before a command stops: one lost datagram says
    /// nothing about the next relay's, but two in a row most likely mean the board is gone, and every further relay
    /// would hold the board (and an interlock OFF behind it) for 2 s more.
    /// </summary>
    public const int MaxUnansweredInARow = 2;

    /// <summary>
    /// How long the client leaves the analog inputs alone after their answer failed: a board that never answers them
    /// costs one time-out per this, not one per poll. A new connection from the poller (a new address or community, or
    /// the board found again) tries at once.
    /// </summary>
    public static readonly TimeSpan AnalogRetryInterval = TimeSpan.FromMinutes(1);

    /// <summary>How many skipped datagrams one try traces; a flood of them would bury the rest of the trace.</summary>
    public const int MaxSkippedTraced = 8;

    /// <summary>How often the restart pins are read again, after the read at the first poll on a connection.</summary>
    public static readonly TimeSpan RulesInterval = TimeSpan.FromSeconds(60);

    private readonly BoardModel _model;
    private readonly DaenetIp2Layout _layout;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DaenetIp2Client> _logger;
    private readonly BoardPoller<DaenetIp2Status> _poller;

    private BoardEndpoint _endpoint;
    private string _community;

    // The socket and where the board is; only used under the poller's lock (a connect, a poll or a command), and by Dispose.
    private Socket? _socket;
    private IPEndPoint? _remote;
    private DaenetIp2Status? _lastTraced;

    // Only used under the poller's lock (a connect, a poll or a command). What the last poll read of the analog inputs,
    // which a command's read takes over, and until when they are left alone after a failed answer.
    private AnalogReadings? _lastAnalog;
    private DateTimeOffset? _analogRetryAt;

    // Only used under the poller's lock. The firmware version as read on this connection (null: not read, or the read
    // failed), the restart pins as last read on it (null: unknown), and when they were last asked for (null: not yet on
    // this connection, or asked for again at the next read).
    private int? _firmware;
    private int? _restartPins;
    private DateTimeOffset? _rulesReadUtc;

    // What the last StateChanged, AnalogChanged or OwnRulesChanged carried, to tell which of the last two a change of
    // the reading alone is. Only on the poller's loop.
    private DaenetIp2Status? _reported;
    private int _wasConnected;
    private int _disposed;
    private int _requestId;

    public DaenetIp2Client(IOptions<DaenetIp2Options> options, BoardModel model, TimeProvider timeProvider, ILogger<DaenetIp2Client> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        if (!DaenetIp2Models.IsDaenetIp2(model))
        {
            throw new ArgumentException($"{model.Name} isn't a DAEnetIP2 v2 kit.", nameof(model));
        }

        var settings = options.Value;
        if (settings.PollSeconds < 1)
        {
            throw new ArgumentException("DaenetIp2:PollSeconds must be at least 1.", nameof(options));
        }

        var community = settings.Community ?? "";
        if (DaenetIp2Models.CheckSendable(community) is { } error)
        {
            throw new ArgumentException($"DaenetIp2:Community can't be sent to the board: {error}", nameof(options));
        }

        _model = model;
        _layout = DaenetIp2Models.Layout(model);
        _endpoint = new BoardEndpoint(settings.Host, settings.Port);
        _community = community;
        _timeProvider = timeProvider;
        _logger = logger;
        _poller = new BoardPoller<DaenetIp2Status>(this, timeProvider, DaenetIp2Status.SwitchesComparer)
        {
            PollInterval = TimeSpan.FromSeconds(settings.PollSeconds),
        };
    }

    /// <summary>
    /// Raised when the relays change, and when the board is lost or answers again; not on a poll that changed nothing. On
    /// the poller's loop: handlers must be quick.
    /// </summary>
    public event EventHandler<DaenetIp2State>? StateChanged;

    /// <summary>
    /// Raised when a poll finds only the analog values changed: the relays and the connection are as before, so
    /// <see cref="StateChanged"/> isn't raised. On the poller's loop: handlers must be quick.
    /// </summary>
    public event EventHandler<DaenetIp2State>? AnalogChanged;

    /// <summary>
    /// Raised when a poll finds only the restart pins changed (<see cref="DaenetIp2Status.RestartPins"/>): the relays and
    /// the connection are as before, so <see cref="StateChanged"/> isn't raised. On the poller's loop: handlers must be
    /// quick.
    /// </summary>
    public event EventHandler<DaenetIp2State>? OwnRulesChanged;

    /// <summary>Every request and answer and every step, in plain words, without the community. Handlers must be quick.</summary>
    public event EventHandler<BoardTraceEntry>? Traced;

    /// <summary>The kit it talks to: which pins to read and set.</summary>
    public BoardModel Model => _model;

    /// <summary>The latest state; refreshed on every poll, so its last contact is always current.</summary>
    public DaenetIp2State State
    {
        get
        {
            var state = _poller.State;
            return new DaenetIp2State(state.IsConnected, state.Reading, state.LastContactUtc);
        }
    }

    /// <summary>Where the client sends: <c>DaenetIp2:Host</c>/<c>Port</c> until <see cref="SetEndpoint"/> moves it.</summary>
    public BoardEndpoint Endpoint => Volatile.Read(ref _endpoint);

    /// <summary>The read-write community it sends; a secret: never log it.</summary>
    public string Community => Volatile.Read(ref _community);

    /// <summary>Moves to another address at once; nothing happens if it is the same.</summary>
    public void SetEndpoint(BoardEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (Interlocked.Exchange(ref _endpoint, endpoint) == endpoint)
        {
            return;
        }

        DaenetIp2Log.EndpointChanged(_logger, endpoint.Host, endpoint.Port);

        // Another board may be at the new address: nothing is known about it until it answers.
        _poller.Forget();
        _poller.Restart();
    }

    /// <summary>
    /// Asks again at once with another community, empty for none; nothing happens if it is the same. The board counts as
    /// connected again only once it answers with it. Throws <see cref="ArgumentException"/> for one the board can't take
    /// (<see cref="DaenetIp2Models.CheckSendable"/>).
    /// </summary>
    public void SetCommunity(string community)
    {
        ArgumentNullException.ThrowIfNull(community);
        if (DaenetIp2Models.CheckSendable(community) is { } error)
        {
            throw new ArgumentException(error.ToString(), nameof(community));
        }

        if (Interlocked.Exchange(ref _community, community) == community)
        {
            return;
        }

        DaenetIp2Log.CommunityChanged(_logger, community.Length == 0 ? "none" : "set");
        _poller.Restart();
    }

    /// <summary>Polls until cancelled. Never throws because of the board. Call it once.</summary>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        return _poller.RunAsync(cancellationToken);
    }

    /// <summary>Turns on the relays in <paramref name="mask"/> (bit 0 = relay 1), one SetRequest each.</summary>
    public Task<ulong> TurnOnAsync(ulong mask, CancellationToken cancellationToken = default)
    {
        return CommandAsync(mask, on: true, cancellationToken);
    }

    /// <summary>Turns off the relays in <paramref name="mask"/> (bit 0 = relay 1), one SetRequest each.</summary>
    public Task<ulong> TurnOffAsync(ulong mask, CancellationToken cancellationToken = default)
    {
        return CommandAsync(mask, on: false, cancellationToken);
    }

    /// <summary>
    /// Turns off the board's own <paramref name="rules"/>, as a person asked: the ICMP monitoring's restart pins of their
    /// relays. Reads <c>cfgP5DupRST</c> again first, in the same command, then writes it once without the pins asked (0
    /// when every pin set is asked), so a pin set at the board since the last read stays as the board has it, and reads it
    /// back. Never writes <c>cfgMode</c> or <c>cfgNewMode</c>. Each pin is a rule of its own, so nothing else goes with a
    /// rule. A rule given twice is taken once. Returns as taken the rules asked whose pin the read-back shows clear,
    /// in relay order, also one whose pin was clear already (nothing is written for it); when the read-back fails, those
    /// the board's echo confirmed. Returns nothing, and sends nothing, for none asked. The next state confirms what is
    /// gone.
    /// Throws <see cref="ArgumentOutOfRangeException"/> for another kind of rule, a relay the kit doesn't have or one on
    /// P3, which has no restart pins, and <see cref="BoardNotConnectedException"/> without a connection, both before
    /// anything is sent; and <see cref="BoardNotConnectedException"/> when the firmware isn't known to have the restart
    /// pins (nothing sent), when they can't be read again (nothing written), when the board refuses the write or keeps
    /// every pin asked, or when it doesn't answer.
    /// </summary>
    public async Task<OwnRulesTurnOff> TurnOffOwnRulesAsync(IReadOnlyCollection<OwnRule> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        List<OwnRule> asked = [];
        foreach (var rule in rules)
        {
            if (rule is null)
            {
                throw new ArgumentException("A rule can't be null.", nameof(rules));
            }

            if (rule.Kind != OwnRuleKind.Restart)
            {
                throw new ArgumentOutOfRangeException(nameof(rules), rule.Kind, "The DAEnetIP2 v2's own rules are its ICMP monitoring's restart pins.");
            }

            if (rule.Relay > _model.Capabilities.RelayCount)
            {
                throw new ArgumentOutOfRangeException(nameof(rules), rule.Relay, $"The {_model.Name} has relays 1–{_model.Capabilities.RelayCount} only.");
            }

            if (!_layout.CanRestart(rule.Relay))
            {
                throw new ArgumentOutOfRangeException(nameof(rules), rule.Relay, $"Relay {rule.Relay} is on P3, which has no restart pins.");
            }

            if (!asked.Contains(rule))
            {
                asked.Add(rule);
            }
        }

        if (asked.Count == 0)
        {
            return OwnRulesTurnOff.None;
        }

        if (!State.IsConnected)
        {
            throw new BoardNotConnectedException();
        }

        try
        {
            var taken = new StrongBox<OwnRulesTurnOff>(OwnRulesTurnOff.None);
            await _poller.RunSwitchesAsync(token => TurnOffRestartPinsAndReadAsync([.. asked.OrderBy(r => r.Relay)], taken, token), cancellationToken).ConfigureAwait(false);
            return taken.Value ?? OwnRulesTurnOff.None;
        }
        catch (Exception ex) when (IsNoAnswer(ex))
        {
            // The poller counted it as a failed poll and reads the board at once.
            throw new BoardNotConnectedException("The DAEnetIP2 v2 board didn't answer; its restart pins may not have been turned off.", ex);
        }
        catch (Exception ex) when (ex is DaenetIp2ProtocolException or BoardRefusedException)
        {
            throw new BoardNotConnectedException($"The DAEnetIP2 v2 board didn't turn its restart pins off: {ex.Message}", ex);
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
        // The poller first, so no new poll or command starts; then nothing opens a new socket. A request already under
        // way goes on until Close disposes its socket under it, and then fails.
        _poller.Dispose();
        Interlocked.Exchange(ref _disposed, 1);
        Close();
    }

    Task IPolledBoard<DaenetIp2Status>.ConnectAsync(CancellationToken cancellationToken)
    {
        // A new connection, maybe to another board or with another community: the analog inputs are read again at once,
        // and so are the firmware version and the restart pins, at the first poll. Nothing is written.
        _lastAnalog = null;
        _analogRetryAt = null;
        _firmware = null;
        _restartPins = null;
        _rulesReadUtc = null;
        return OpenAsync(cancellationToken);
    }

    Task<DaenetIp2Status> IPolledBoard<DaenetIp2Status>.ReadAsync(CancellationToken cancellationToken)
    {
        return ReadStatusAsync(readAnalog: true, RulesRead.IfDue, cancellationToken);
    }

    void IPolledBoard<DaenetIp2Status>.OnStateChanged(PolledState<DaenetIp2Status> state)
    {
        var wasConnected = Interlocked.Exchange(ref _wasConnected, state.IsConnected ? 1 : 0) == 1;
        if (state.IsConnected && !wasConnected)
        {
            var endpoint = Endpoint;
            DaenetIp2Log.Connected(_logger, endpoint.Host, endpoint.Port);
        }

        _reported = state.Reading;
        Raise(StateChanged, new DaenetIp2State(state.IsConnected, state.Reading, state.LastContactUtc));
    }

    void IPolledBoard<DaenetIp2Status>.OnReadingChanged(PolledState<DaenetIp2Status> state)
    {
        // The comparer leaves out the analog values and the restart pins: one of them changed, or both.
        var before = _reported;
        _reported = state.Reading;
        var changed = new DaenetIp2State(state.IsConnected, state.Reading, state.LastContactUtc);
        var rulesChanged = before?.RestartPins != state.Reading?.RestartPins;
        if (rulesChanged)
        {
            Raise(OwnRulesChanged, changed);
        }

        if (!rulesChanged || before?.Analog != state.Reading?.Analog)
        {
            Raise(AnalogChanged, changed);
        }
    }

    void IPolledBoard<DaenetIp2Status>.OnStep(BoardPollStep kind, string? detail)
    {
        switch (kind)
        {
            case BoardPollStep.Connecting:
                ForgetLastStatus();
                Trace(BoardTraceDirection.None, $"Asking {Endpoint} over SNMP");
                break;
            case BoardPollStep.Lost:
                ForgetLastStatus();
                DaenetIp2Log.ConnectionLost(_logger, detail);
                Trace(BoardTraceDirection.None, $"Connection lost: {detail}", isError: true);
                break;
            case BoardPollStep.Waiting:
                DaenetIp2Log.Reconnecting(_logger, detail);
                Trace(BoardTraceDirection.None, $"Trying again in {detail} s");
                break;
            case BoardPollStep.Stopped:
                ForgetLastStatus();
                Trace(BoardTraceDirection.None, "The app stopped asking");
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

    // The seconds of RequestTimeout as a message says them: "1".
    private static string TimeoutSeconds => RequestTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture);

    // What a request without an answer says: a wrong community gets no answer at all in SNMPv1.
    private static string NoAnswerMessage =>
        $"The DAEnetIP2 v2 board didn't answer within {TimeoutSeconds} s, asked {Attempts.ToString(CultureInfo.InvariantCulture)} times: check the address and the community.";

    // What an analog input's request without an answer says: the ports answered, so the address and community are right.
    private static string AnalogNoAnswerMessage =>
        $"The DAEnetIP2 v2 board didn't answer for an analog input within {TimeoutSeconds} s, asked {Attempts.ToString(CultureInfo.InvariantCulture)} times.";

    // The next request id, from 1 up to int.MaxValue, and then from 1 again.
    private int NextRequestId()
    {
        while (true)
        {
            var id = Interlocked.Increment(ref _requestId);
            if (id > 0)
            {
                return id;
            }

            Interlocked.CompareExchange(ref _requestId, 0, id);
        }
    }

    private async Task<ulong> CommandAsync(ulong mask, bool on, CancellationToken cancellationToken)
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

        // A poll under way gives way. The board switches one pin per request, and the whole mask goes out, as one
        // command on a board that takes a mask would: a group's interlock and "All off" rely on every OFF being sent,
        // also after one that got no answer: over UDP one lost datagram says nothing about the next.
        try
        {
            var taken = new StrongBox<ulong>();
            await _poller.RunSwitchesAsync(token => SwitchEachAsync(mask, on, taken, token), cancellationToken).ConfigureAwait(false);
            return taken.Value;
        }
        catch (Exception ex) when (IsNoAnswer(ex))
        {
            // The poller counted it as a failed poll and reads the board at once, which shows what it did.
            throw new BoardNotConnectedException("The DAEnetIP2 v2 board didn't answer; the command may not have been carried out.", ex);
        }
        catch (Exception ex) when (ex is DaenetIp2ProtocolException or BoardRefusedException)
        {
            // No relay was taken. The poller read the board at once, and counted it as a failed poll unless the board
            // refused every relay (BoardRefusedException): that is an answer. Outside the poller a refusal is "didn't
            // take the command", as on every board.
            throw new BoardNotConnectedException($"The DAEnetIP2 v2 board didn't take the command: {ex.Message}", ex);
        }
    }

    // Under the poller's lock. Returns the board's state read after the switches, which the poller takes as a poll's,
    // or none when that read failed, and puts the relays the board took in <paramref name="taken"/>.
    private async Task<CommandReading<DaenetIp2Status>> SwitchEachAsync(ulong mask, bool on, StrongBox<ulong> taken, CancellationToken cancellationToken)
    {
        string? refusal = null;
        var unreadable = false;
        Exception? noAnswer = null;
        var unansweredInARow = 0;
        var gone = false;
        var community = Community;
        foreach (var relay in new ChannelMask(mask).Channels)
        {
            var request = DaenetIp2Snmp.SetPin(NextRequestId(), community, _layout.PinOf(relay), on);

            try
            {
                var (value, pdu) = await ExchangeAsync(request, routine: false, cancellationToken).ConfigureAwait(false);
                Trace(BoardTraceDirection.Received, DaenetIp2TraceText.Confirmed(request.Oid, value, _layout), pdu);
                taken.Value |= 1UL << (relay - 1);
                unansweredInARow = 0;
            }
            catch (Exception ex) when (ex is DaenetIp2RefusedException or DaenetIp2ProtocolException)
            {
                // Refused, or an answer that can't be read: the relay may or may not have switched. The others still
                // go; the read after them shows the truth.
                refusal ??= ex.Message;
                unreadable |= ex is DaenetIp2ProtocolException;
                unansweredInARow = 0;
                Trace(
                    BoardTraceDirection.None,
                    string.Create(CultureInfo.InvariantCulture, $"The board didn't take the command for relay {relay}: {ex.Message}"),
                    isError: true);
            }
            catch (Exception ex) when (IsNoAnswer(ex))
            {
                // No answer, also when asked again: the relay may or may not have switched. One lost datagram says
                // nothing about the next, so the others still go, as "All off" and a group's interlock need; the read
                // after them shows the truth. A second relay in a row without an answer: the board is most likely
                // gone, and the rest would only hold the board's lock longer.
                noAnswer ??= ex;
                if (++unansweredInARow >= MaxUnansweredInARow)
                {
                    gone = true;
                    Trace(
                        BoardTraceDirection.None,
                        string.Create(CultureInfo.InvariantCulture, $"No answer for relay {relay} either: the board is most likely gone, so the rest isn't sent"),
                        isError: true);
                    break;
                }

                Trace(
                    BoardTraceDirection.None,
                    string.Create(CultureInfo.InvariantCulture, $"No answer for relay {relay}: going on with the rest"),
                    isError: true);
            }
        }

        // Not one relay was taken: the command failed, as on a board that takes the mask at once, and nothing is
        // counted as switched. The poller reads the board at once, which shows what really happened. A request with
        // no answer counts as a failed poll, and so does an answer that couldn't be read, as a poll's would. When the
        // board refused every relay with an error-status, it answered: that doesn't count toward losing it.
        if (taken.Value == 0)
        {
            if (noAnswer is not null)
            {
                ExceptionDispatchInfo.Throw(noAnswer);
            }

            throw unreadable ? new DaenetIp2ProtocolException(refusal!) : new BoardRefusedException(refusal!);
        }

        // Stopped as the board is most likely gone: no read, which would only hold the lock 2 s more. It isn't an
        // answered poll: the poller keeps the last state read, and reads the board at once, as an ordinary poll.
        if (gone)
        {
            return default;
        }

        try
        {
            return new CommandReading<DaenetIp2Status>(await ReadStatusAsync(readAnalog: false, RulesRead.Never, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is DaenetIp2RefusedException or DaenetIp2ProtocolException || IsNoAnswer(ex))
        {
            // Never a state the board didn't read: an echo doesn't show that the relay switched (the board ignores a
            // set while its ICMP monitoring or an analog event holds the pin, manual section 7). The command doesn't
            // fail (what was taken was sent), and it isn't an answered poll: the poller keeps the last state read and
            // reads the board at once, which counts as a poll of its own if it fails too.
            return default;
        }
    }

    // Under the poller's lock: cfgP5DupRST read again, then written once without the pins asked, then the board's state
    // read back with it. Puts what went off in taken.
    private async Task<CommandReading<DaenetIp2Status>> TurnOffRestartPinsAndReadAsync(IReadOnlyList<OwnRule> asked, StrongBox<OwnRulesTurnOff> taken, CancellationToken cancellationToken)
    {
        // Only a firmware read on this connection to have them: nothing goes out otherwise. Not an answer, and not a
        // failure of the board.
        if (_firmware is not >= DaenetIp2Api.RestartPinsFirmware)
        {
            throw new BoardNotConnectedException("The DAEnetIP2 v2 board's firmware isn't known to have restart pins (4.097 or later); nothing was sent.");
        }

        // Whatever comes of it, the next read reads the pins again.
        _rulesReadUtc = null;
        var community = Community;
        var askedPins = asked.Aggregate(0, (mask, rule) => mask | _layout.PinOf(rule.Relay).Bit);

        // Read again right before the write, so a pin set at the board since the last read is written as the board has it.
        // A pin that no relay is on, or another relay's, is never cleared here. No answer to both tries, or an answer that
        // can't be read, is thrown as it is: the poller counts it as a failed poll, and nothing was written. A refusal is
        // an answer: the board is there.
        int now;
        try
        {
            now = await ReadRestartPinsAsync(community, cancellationToken).ConfigureAwait(false);
        }
        catch (DaenetIp2RefusedException ex)
        {
            _restartPins = null;
            throw new BoardRefusedException($"{ex.Message} Nothing was written.");
        }

        _restartPins = now;
        var echoed = now;
        var write = now & ~askedPins;
        if (write != now)
        {
            // An answer that can't be read (the board may or may not have taken it) goes as it is: the poller counts it
            // and reads at once, the pins included.
            var request = DaenetIp2Snmp.SetRestartPins(NextRequestId(), community, write);
            try
            {
                var (value, pdu) = await ExchangeAsync(request, routine: false, cancellationToken).ConfigureAwait(false);
                Trace(BoardTraceDirection.Received, DaenetIp2TraceText.Confirmed(request.Oid, value, _layout), pdu);
                echoed = value;
            }
            catch (DaenetIp2RefusedException ex)
            {
                throw new BoardRefusedException(ex.Message);
            }
        }

        // The read-back: the state, with the pins read again. A pin the read-back shows clear went off; without a
        // read-back, the board's echo of the write says it.
        DaenetIp2Status? status = null;
        try
        {
            status = await ReadStatusAsync(readAnalog: false, RulesRead.Now, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DaenetIp2RefusedException or DaenetIp2ProtocolException || IsNoAnswer(ex))
        {
            // The write went out; the poller keeps the last state and reads again at once, the pins included.
        }

        var after = status?.RestartPins ?? echoed;
        List<OwnRule> done = [.. asked.Where(r => (after & _layout.PinOf(r.Relay).Bit) == 0)];
        if (done.Count == 0)
        {
            throw new BoardRefusedException("The board kept the restart pins it was asked to turn off.");
        }

        taken.Value = OwnRulesTurnOff.Of(done);
        return status is null ? default : new CommandReading<DaenetIp2Status>(status);
    }

    // Only in a poll (readAnalog), the analog inputs first, so a command's confirmation never waits on them: a command's
    // read keeps what the last poll read of them. Then a GetRequest of each port the relays are on, P3 before P5, so the
    // relays the poll reports are as fresh as they can be, however long the analog inputs took. A pin that is High is a
    // relay that is on. Last, when they are asked for or due, the restart pins: after the ports, so a board that doesn't
    // answer them can't hold the relays back.
    private async Task<DaenetIp2Status> ReadStatusAsync(bool readAnalog, RulesRead rules, CancellationToken cancellationToken)
    {
        var last = Volatile.Read(ref _lastTraced);
        var community = Community;
        var analog = _lastAnalog;
        if (readAnalog && _model.Capabilities.Analog is { } ability)
        {
            analog = await ReadAnalogAsync(ability, last?.Analog, community, cancellationToken).ConfigureAwait(false);
        }

        var p3 = 0;
        var p5 = 0;
        foreach (var port in _layout.Ports)
        {
            var (value, pdu) = await ExchangeAsync(DaenetIp2Snmp.GetPort(NextRequestId(), community, port), routine: true, cancellationToken).ConfigureAwait(false);
            Trace(BoardTraceDirection.Received, DaenetIp2TraceText.Port(port, value), pdu, isRoutine: true);
            if (port == DaenetIp2Port.P3)
            {
                p3 = value;
            }
            else
            {
                p5 = value;
            }
        }

        var relays = _layout.RelaysOf(p3, p5);
        Trace(BoardTraceDirection.Received, DaenetIp2TraceText.Relays(relays, _model.Capabilities.RelayCount), isRoutine: last?.Relays == relays);
        _lastAnalog = analog;
        if (rules == RulesRead.Now
            || (rules == RulesRead.IfDue && (_rulesReadUtc is not { } readUtc || _timeProvider.GetUtcNow() - readUtc >= RulesInterval)))
        {
            _restartPins = await ReadRulesAsync(community, cancellationToken).ConfigureAwait(false);
            _rulesReadUtc = _timeProvider.GetUtcNow();
        }

        var status = new DaenetIp2Status(relays, analog, _restartPins);
        Volatile.Write(ref _lastTraced, status);
        return status;
    }

    // The restart pins, once the firmware version read on this connection says the board has them (4.097 and later);
    // null otherwise: unknown, never a guess. The version is read once per connection, again after a read that failed.
    // Never fails the poll; only a cancelled read (a command takes the poll's place, or the app stops) is thrown.
    private async Task<int?> ReadRulesAsync(string community, CancellationToken cancellationToken)
    {
        if (_firmware is null)
        {
            var request = DaenetIp2Snmp.GetFirmwareVersion(NextRequestId(), community);
            try
            {
                var (value, pdu) = await ExchangeAsync(request, routine: true, cancellationToken).ConfigureAwait(false);
                Trace(BoardTraceDirection.Received, DaenetIp2TraceText.Firmware(value), pdu, isRoutine: true);
                _firmware = value;
            }
            catch (Exception ex) when (ex is DaenetIp2RefusedException or DaenetIp2ProtocolException || IsNoAnswer(ex))
            {
                // A refusal or no answer is traced by ExchangeAsync; this says what it means.
                Trace(BoardTraceDirection.None, $"Couldn't read the firmware version, so the restart pins aren't read: {ex.Message}", isError: true);
                return null;
            }
        }

        if (_firmware < DaenetIp2Api.RestartPinsFirmware)
        {
            // Said once per connection, as the version is read once.
            if (_rulesReadUtc is null)
            {
                Trace(
                    BoardTraceDirection.None,
                    $"Firmware {DaenetIp2Api.FirmwareText(_firmware.Value)} is older than {DaenetIp2Api.FirmwareText(DaenetIp2Api.RestartPinsFirmware)}: the app can't read its restart pins",
                    isRoutine: true);
            }

            return null;
        }

        try
        {
            return await ReadRestartPinsAsync(community, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DaenetIp2RefusedException or DaenetIp2ProtocolException || IsNoAnswer(ex))
        {
            Trace(BoardTraceDirection.None, $"Couldn't read the restart pins: {ex.Message}", isError: true);
            return null;
        }
    }

    // cfgP5DupRST, 0 to 255. Throws as ExchangeAsync does.
    private async Task<int> ReadRestartPinsAsync(string community, CancellationToken cancellationToken)
    {
        var (value, pdu) = await ExchangeAsync(DaenetIp2Snmp.GetRestartPins(NextRequestId(), community), routine: true, cancellationToken).ConfigureAwait(false);
        Trace(BoardTraceDirection.Received, DaenetIp2TraceText.RestartPins(value, _layout), pdu, isRoutine: _restartPins == value);
        return value;
    }

    // The analog inputs, one GetRequest each (the board takes one OID per request, section 9), or "no reading" for each
    // when an answer is missing, refused or can't be read (another type, a value outside 0 to 1023), or while they are
    // left alone after such an answer (AnalogRetryInterval). That never fails the poll: an unproven measurement must not
    // take away the relays. A late answer can't be taken for a later one: each request has its own id, and
    // ReceiveAsync skips any other. A cancellation (a command takes the poll's place) still ends the poll, as for any
    // request.
    private async Task<AnalogReadings> ReadAnalogAsync(AnalogAbility ability, AnalogReadings? last, string community, CancellationToken cancellationToken)
    {
        var none = new AnalogReadings(new int?[ability.Count]);
        if (_analogRetryAt is { } retryAt && _timeProvider.GetUtcNow() < retryAt)
        {
            return none;
        }

        _analogRetryAt = null;
        var counts = new int?[ability.Count];
        string? problem = null;
        for (var input = 1; input <= ability.Count && problem is null; input++)
        {
            var request = DaenetIp2Snmp.GetAnalog(NextRequestId(), community, input);
            try
            {
                var (value, pdu) = await ExchangeAsync(request, routine: true, cancellationToken).ConfigureAwait(false);
                Trace(BoardTraceDirection.Received, DaenetIp2TraceText.Analog(input, value, ability), pdu, isRoutine: last?.Input(input) == value);
                counts[input - 1] = value;
            }
            catch (DaenetIp2RefusedException ex)
            {
                problem = string.Create(CultureInfo.InvariantCulture, $"The board refused analog input {input}: {DaenetIp2TraceText.ErrorName(ex.ErrorStatus)}.");
            }
            catch (DaenetIp2ProtocolException ex)
            {
                problem = string.Create(CultureInfo.InvariantCulture, $"The board's answer for analog input {input} can't be read: {ex.Message}");
            }
            catch (Exception ex) when (IsNoAnswer(ex))
            {
                problem = string.Create(CultureInfo.InvariantCulture, $"The board didn't answer for analog input {input}.");
            }
        }

        if (problem is null)
        {
            return new AnalogReadings(counts);
        }

        _analogRetryAt = _timeProvider.GetUtcNow() + AnalogRetryInterval;
        DaenetIp2Log.AnalogNotRead(_logger, problem);
        Trace(
            BoardTraceDirection.None,
            string.Create(CultureInfo.InvariantCulture, $"No analog readings: {problem} The relays are read as usual; the analog inputs are asked for again in {AnalogRetryInterval.TotalMinutes} min."),
            isError: true);
        return none;
    }

    /// <summary>
    /// Sends one request and waits for its answer, <see cref="RequestTimeout"/> each time, <see cref="Attempts"/> times
    /// with the same request id: a set or a get does the same when done twice. Returns the answer's value once
    /// <see cref="DaenetIp2Snmp.Value"/> has checked it, with its PDU for the caller's trace. Traces the request,
    /// what was skipped and anything but a good answer. Throws <see cref="TimeoutException"/> when no answer came,
    /// <see cref="DaenetIp2RefusedException"/> for an error-status and <see cref="DaenetIp2ProtocolException"/> for an
    /// answer that isn't one to it.
    /// </summary>
    private async Task<(int Value, string Pdu)> ExchangeAsync(DaenetIp2Request request, bool routine, CancellationToken cancellationToken)
    {
        try
        {
            // None after an earlier request broke it.
            if (_socket is null)
            {
                await OpenAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsNoAnswer(ex))
        {
            Close();
            Trace(BoardTraceDirection.None, $"No answer: {ex.Message}", isError: true);
            throw;
        }

        var socket = _socket!;
        var remote = _remote!;
        var words = DaenetIp2TraceText.Request(request, _layout);
        var pdu = DaenetIp2Snmp.Hex(request.Pdu);
        for (var attempt = 1; ; attempt++)
        {
            Trace(BoardTraceDirection.Sent, words, pdu, isRoutine: routine);
            using var timeout = new CancellationTokenSource(RequestTimeout, _timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            DaenetIp2Message answer;
            try
            {
                // A datagram goes whole or not at all.
                await socket.SendToAsync(request.Message, SocketFlags.None, remote, linked.Token).ConfigureAwait(false);
                answer = await ReceiveAsync(socket, remote, request, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                if (attempt < Attempts)
                {
                    Trace(
                        BoardTraceDirection.None,
                        $"No answer within {TimeoutSeconds} s: asking again",
                        isError: true,
                        isRoutine: routine);
                    continue;
                }

                var message = DaenetIp2Api.AnalogInputOf(request.Oid) is null ? NoAnswerMessage : AnalogNoAnswerMessage;
                Trace(BoardTraceDirection.None, $"No answer: {message}", isError: true);
                throw new TimeoutException(message);
            }
            catch (SocketException ex)
            {
                // The network refused the datagram: a socket that may be broken is opened afresh by the next request.
                Close();
                Trace(BoardTraceDirection.None, $"No answer: {ex.Message}", isError: true);
                throw;
            }

            var answerPdu = DaenetIp2Snmp.Hex(answer.Pdu);
            try
            {
                return (DaenetIp2Snmp.Value(answer, request), answerPdu);
            }
            catch (DaenetIp2RefusedException ex)
            {
                Trace(BoardTraceDirection.Received, DaenetIp2TraceText.Refused(ex.ErrorStatus), answerPdu, isError: true);
                throw;
            }
            catch (DaenetIp2ProtocolException ex)
            {
                Trace(BoardTraceDirection.Received, $"Unreadable answer: {ex.Message}", answerPdu, isError: true);
                throw;
            }
        }
    }

    // Reads datagrams until one from the board carries the request's id; skips the rest. Cancelled by the timeout.
    private async Task<DaenetIp2Message> ReceiveAsync(Socket socket, IPEndPoint remote, DaenetIp2Request request, CancellationToken cancellationToken)
    {
        // One byte more than any message: a datagram that fills it is too long to be one.
        var buffer = new byte[DaenetIp2Api.MaxMessageBytes + 1];
        EndPoint any = new IPEndPoint(remote.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        var skipped = 0;
        while (true)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                // Windows reports an ICMP "port unreachable" for an earlier datagram, or a datagram too long for the
                // buffer, on the next receive: neither is the answer.
                TraceSkipped(ref skipped, BoardTraceDirection.None, $"Skipped: {ex.Message}", isRoutine: true);
                continue;
            }

            if (received.RemoteEndPoint is not IPEndPoint from || !from.Address.Equals(remote.Address) || from.Port != remote.Port)
            {
                TraceSkipped(ref skipped, BoardTraceDirection.Received, $"Skipped a datagram from {received.RemoteEndPoint}: not the board");
                continue;
            }

            if (received.ReceivedBytes > DaenetIp2Api.MaxMessageBytes)
            {
                TraceSkipped(
                    ref skipped,
                    BoardTraceDirection.Received,
                    string.Create(CultureInfo.InvariantCulture, $"Skipped a datagram longer than {DaenetIp2Api.MaxMessageBytes} bytes"));
                continue;
            }

            DaenetIp2Message message;
            try
            {
                message = DaenetIp2Snmp.Decode(buffer.AsSpan(0, received.ReceivedBytes));
            }
            catch (DaenetIp2ProtocolException ex)
            {
                // Its bytes may hold a community: only its length shows.
                TraceSkipped(
                    ref skipped,
                    BoardTraceDirection.Received,
                    string.Create(CultureInfo.InvariantCulture, $"Skipped an unreadable datagram of {received.ReceivedBytes} bytes: {ex.Message}"));
                continue;
            }

            if (message.RequestId != request.RequestId)
            {
                TraceSkipped(ref skipped, BoardTraceDirection.Received, "Skipped a late answer nobody waited for", DaenetIp2Snmp.Hex(message.Pdu), isError: false, isRoutine: true);
                continue;
            }

            return message;
        }
    }

    // Traces a datagram that isn't the answer: the first MaxSkippedTraced of one try, then one line for the rest.
    private void TraceSkipped(ref int skipped, BoardTraceDirection direction, string text, string? bytes = null, bool isError = true, bool isRoutine = false)
    {
        skipped++;
        if (skipped <= MaxSkippedTraced)
        {
            Trace(direction, text, bytes, isError, isRoutine);
        }
        else if (skipped == MaxSkippedTraced + 1)
        {
            Trace(BoardTraceDirection.Received, "Skipping more datagrams that aren't the answer", isError: true);
        }
    }

    // Finds the board and opens a socket for it, closing any before. Throws TimeoutException or SocketException.
    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        Close();
        ThrowIfDisposed();
        var endpoint = Endpoint;

        // An empty name would find this computer's own addresses: no board is there.
        if (!endpoint.HasHost)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        if (!IPAddress.TryParse(endpoint.Host, out var address))
        {
            using var timeout = new CancellationTokenSource(RequestTimeout, _timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            IPAddress[] addresses;
            try
            {
                addresses = await Dns.GetHostAddressesAsync(endpoint.Host, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"The name {endpoint.Host} wasn't found within {TimeoutSeconds} s.");
            }

            address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6)
                ?? throw new SocketException((int)SocketError.HostNotFound);
        }

        var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(address.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        // Kept with a full fence before the check below, against Dispose's flag before its Close: either that Close
        // finds this socket, or the check finds the flag.
        _remote = new IPEndPoint(address, endpoint.Port);
        Interlocked.Exchange(ref _socket, socket);

        // Disposed while the name was looked up: Dispose's Close may have come before the socket was kept.
        if (Volatile.Read(ref _disposed) == 1)
        {
            Close();
            ThrowIfDisposed();
        }

        var local = (IPEndPoint)socket.LocalEndPoint!;
        Trace(BoardTraceDirection.None, string.Create(CultureInfo.InvariantCulture, $"Sending to {_remote} from UDP port {local.Port}"));
    }

    private void Close()
    {
        var socket = Interlocked.Exchange(ref _socket, null);
        _remote = null;
        socket?.Dispose();
    }

    // Once disposed, a request still under way gets no answer: it fails as one, and opens no new connection.
    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new IOException("The app closed the connection to the board.");
        }
    }

    // The first status after the client (re)starts always gets a toast.
    private void ForgetLastStatus()
    {
        Volatile.Write(ref _lastTraced, null);
    }

    private void Trace(BoardTraceDirection direction, string text, string? bytes = null, bool isError = false, bool isRoutine = false)
    {
        Raise(Traced, new BoardTraceEntry(_timeProvider.GetUtcNow(), direction, text, bytes, isError, isRoutine));
    }

    // Which reads read the restart pins: a poll when they are due, a turn-off's read-back always, a switch's never.
    private enum RulesRead
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
                DaenetIp2Log.SubscriberFailed(_logger, ex);
            }
        }
    }
}
