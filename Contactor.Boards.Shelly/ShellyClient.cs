using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Contactor.Boards.Shelly;

/// <summary>
/// Talks to a Shelly Gen2+ device over HTTP with JSON-RPC (<see cref="ShellyApi"/>): one <c>POST /rpc</c> per call,
/// with the device's own digest authentication (<see cref="ShellyDigest"/>). Polls <c>Shelly.GetStatus</c> through
/// <see cref="BoardPoller{TReading}"/> and switches one relay per <c>Switch.Set</c>. One request at a time (the poller
/// sees to it). Commands are never queued: without a connection they fail at once with
/// <see cref="BoardNotConnectedException"/>, a poll under way gives way to them, and the device's state right after
/// them is reported at once.
/// <para>
/// Every new connection first asks <c>Shelly.GetDeviceInfo</c>, which answers without a password, and refuses in plain
/// words a device that isn't Gen2+, one in the cover profile, and firmware before 1.7.0. Then it asks
/// <c>Shelly.GetStatus</c>, with the password when the device asks for one, and refuses a device with another number of
/// switches than the chosen entry. The device counts as connected only once a poll after that answered.
/// </para>
/// <para>
/// Firmware before 2.0.0 needs a new challenge for every request: each call goes once without a password, gets its
/// 401, and goes again with the digest. From 2.0.0 the client keeps the nonce and sends the next calls with it at once,
/// with a growing <c>nc</c>. When the device refuses a kept nonce (it expired, was used up or forgotten), the call goes
/// once more with the new challenge that came with the refusal; a refusal of that one is a wrong password. No call
/// goes more than twice, and a 429 is never tried again within a call. The password and the digest are never logged
/// or traced.
/// </para>
/// <para>
/// Each switch's state after a power cut is its <c>initial_state</c>, read with one <c>Switch.GetConfig</c> per switch
/// at connect, and in a poll only when <c>sys.cfg_rev</c> differs from the one it was read with, or every
/// <see cref="SettingsInterval"/> while that can't tell (no <c>cfg_rev</c>, or a read that failed): never on every poll,
/// and never in the read that confirms a <c>Switch.Set</c>. A settings read that fails never fails the connect or the
/// poll: the switches it couldn't read are unknown. <see cref="SetPowerOnAsync"/> sets it for one switch with
/// <c>Switch.SetConfig</c> and reads it back at once.
/// </para>
/// <para>
/// The device's own rules (<see cref="ShellyStatus.OwnRules"/>) come from the same <c>Switch.GetConfig</c> (each switch's
/// <c>auto_on</c>) and from <c>Schedule.List</c>, read at connect, and in a poll only when <c>sys.schedule_rev</c>
/// differs from the one it was read with, or every <see cref="SettingsInterval"/> while that can't tell; never in the
/// read that confirms a <c>Switch.Set</c>. A read that fails makes the rules unknown, never the device lost.
/// <see cref="TurnOffOwnRulesAsync"/> turns them off with <c>Schedule.Update</c> and <c>Switch.SetConfig</c>, only on a
/// person's press, and reads them back at once.
/// </para>
/// </summary>
public sealed class ShellyClient : IPolledBoard<ShellyStatus>, IDisposable, IAsyncDisposable
{
    /// <summary>How long a request may take before it counts as unanswered.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The most requests one nonce is used for: "A single nonce can be reused for up to 30,000 requests"
    /// ("Authentication"). After that the client asks for a new one instead of waiting for the refusal.
    /// </summary>
    public const int MaxNonceCount = 30_000;

    /// <summary>The most requests the client uses one nonce for: <see cref="MaxNonceCount"/>, lower only in the tests.</summary>
    internal int NonceLimit { get; set; } = MaxNonceCount;

    /// <summary>
    /// How often a poll reads the switches' settings again while <c>cfg_rev</c> can't tell whether they changed: the
    /// device gives none, or the last read failed for a switch.
    /// </summary>
    public static readonly TimeSpan SettingsInterval = TimeSpan.FromSeconds(60);

    private const int MaxResponseHeadersKilobytes = 8;

    private readonly BoardModel _model;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ShellyClient> _logger;
    private readonly HttpClient _http;
    private readonly BoardPoller<ShellyStatus> _poller;

    private BoardEndpoint _endpoint;
    private string _password;
    private ShellyStatus? _lastTraced;
    private int _wasConnected;

    // Only used under the poller's lock (a connect, a poll or a command): the next request id, and the digest state of
    // the current connection, which ConnectAsync starts afresh.
    private int _nextId;
    private bool _reuseNonce;
    private ShellyChallenge? _nonce;
    private BoardEndpoint? _nonceEndpoint;
    private int _nonceCount;

    // Only used under the poller's lock: the switches' states after a power cut as last read, the cfg_rev they were read
    // with, whether they are read for the current connection, when, and whether a switch failed. ConnectAsync and a
    // SetConfig start them afresh.
    private PowerOnStates? _powerOn;
    private long? _configRevision;
    private bool _configRead;
    private bool _configFailed;
    private DateTimeOffset _configReadUtc;

    // Only used under the poller's lock: each switch's auto_on as last read with its settings (null for one it couldn't
    // read; the array null before the first read), and the schedules as last read, with the schedule_rev they were read
    // with, whether they are read for the current connection, when, and whether the read failed. ConnectAsync starts
    // them afresh.
    private bool?[]? _autoOn;
    private ShellySchedules? _schedules;
    private long? _scheduleRevision;
    private bool _schedulesRead;
    private bool _schedulesFailed;
    private DateTimeOffset _schedulesReadUtc;

    // The reading last reported, to tell which part of it changed when the poller's comparer sees no change.
    private ShellyStatus? _lastReported;

    public ShellyClient(IOptions<ShellyOptions> options, BoardModel model, TimeProvider timeProvider, ILogger<ShellyClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        if (!ShellyModels.IsShelly(model))
        {
            throw new ArgumentException($"{model.Name} isn't a Shelly.", nameof(model));
        }

        var settings = options.Value;
        if (settings.PollSeconds < 1)
        {
            throw new ArgumentException("Shelly:PollSeconds must be at least 1.", nameof(options));
        }

        // Fail at start, not on the first connection, if the password can't be taken.
        var password = settings.Password ?? "";
        if (ShellyModels.CheckPassword(password) is { } error)
        {
            throw new ArgumentException($"Shelly:Password can't be used: {error}", nameof(options));
        }

        _model = model;
        _password = password;
        _endpoint = new BoardEndpoint(settings.Host, settings.Port);
        _timeProvider = timeProvider;
        _logger = logger;

        // The device is on the local network: never through a proxy. It closes every connection after its answer
        // ("RPC Channels": no keepalive).
        _http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            MaxConnectionsPerServer = 1,
            MaxResponseHeadersLength = MaxResponseHeadersKilobytes,
            PooledConnectionLifetime = TimeSpan.Zero,
        })
        {
            // The requests time out with _timeProvider instead, see PostAsync.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        // A change of a power-on state alone is no state change: it comes as PowerOnChanged.
        _poller = new BoardPoller<ShellyStatus>(this, timeProvider, ShellyStatus.RelaysComparer)
        {
            PollInterval = TimeSpan.FromSeconds(settings.PollSeconds),
        };
    }

    /// <summary>
    /// Raised when the relays change, and when the connection is lost or comes back; not on a poll that changed nothing.
    /// On the poller's loop: handlers must be quick.
    /// </summary>
    public event EventHandler<ShellyState>? StateChanged;

    /// <summary>
    /// Raised when only the switches' states after a power cut changed (<see cref="ShellyState.PowerOn"/>), not the
    /// relays or the connection, which come as <see cref="StateChanged"/>. On the poller's loop: handlers must be quick.
    /// </summary>
    public event EventHandler<ShellyState>? PowerOnChanged;

    /// <summary>
    /// Raised when only the device's own rules changed (<see cref="ShellyState.OwnRules"/>), not the relays or the
    /// connection, which come as <see cref="StateChanged"/>. On the poller's loop: handlers must be quick.
    /// </summary>
    public event EventHandler<ShellyState>? OwnRulesChanged;

    /// <summary>
    /// Every request and answer and every connection step, in plain words. The password and the digest never show. On
    /// the poller's loop or a command's: handlers must be quick.
    /// </summary>
    public event EventHandler<BoardTraceEntry>? Traced;

    /// <summary>The entry it talks to: how many switches the device must have.</summary>
    public BoardModel Model => _model;

    /// <summary>The latest state; refreshed on every poll, so its last contact is always current.</summary>
    public ShellyState State
    {
        get
        {
            var state = _poller.State;
            return new ShellyState(state.IsConnected, state.Reading, state.LastContactUtc, state.Problem);
        }
    }

    /// <summary>Where the client connects: <c>Shelly:Host</c>/<c>Port</c> until <see cref="SetEndpoint"/> moves it.</summary>
    public BoardEndpoint Endpoint => Volatile.Read(ref _endpoint);

    /// <summary>The device's password; empty for none. A secret: never log it.</summary>
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

        // Another device may be at the new address: nothing is known about it until it answers.
        _poller.Forget();
        _poller.Restart();
    }

    /// <summary>
    /// Connects again at once with another password, empty for none; nothing happens if it is the same. Throws
    /// <see cref="ArgumentException"/> for one the app can't take (<see cref="ShellyModels.CheckPassword"/>).
    /// </summary>
    public void SetPassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (ShellyModels.CheckPassword(password) is { } error)
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

    /// <summary>
    /// A person's "Check again": connects again at once while the Shelly isn't connected, also after it refused the
    /// password, which isn't tried again otherwise. Nothing happens while it is connected.
    /// </summary>
    public void TryAgain()
    {
        _poller.TryAgain();
    }

    /// <summary>Connects and polls until cancelled. Never throws because of the device. Call it once.</summary>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        return _poller.RunAsync(cancellationToken);
    }

    /// <summary>Turns on the relays in <paramref name="mask"/> (bit 0 = relay 1), one <c>Switch.Set</c> each.</summary>
    public Task<byte> TurnOnAsync(byte mask, CancellationToken cancellationToken = default)
    {
        return CommandAsync(mask, on: true, cancellationToken);
    }

    /// <summary>Turns off the relays in <paramref name="mask"/> (bit 0 = relay 1), one <c>Switch.Set</c> each.</summary>
    public Task<byte> TurnOffAsync(byte mask, CancellationToken cancellationToken = default)
    {
        return CommandAsync(mask, on: false, cancellationToken);
    }

    /// <summary>
    /// Sets what <paramref name="relay"/> does at power on to <paramref name="state"/> with one <c>Switch.SetConfig</c>
    /// for that switch only, then reads every switch's <c>initial_state</c> back in the same command, which the poller
    /// takes as the new state. Throws <see cref="ArgumentOutOfRangeException"/> for a relay the entry hasn't or a state
    /// the device hasn't, and <see cref="BoardNotConnectedException"/> without a connection, both before anything is
    /// sent; and <see cref="BoardNotConnectedException"/> when the device didn't take it or didn't answer.
    /// </summary>
    public async Task SetPowerOnAsync(int relay, PowerOnState state, CancellationToken cancellationToken = default)
    {
        _model.Capabilities.Relay(relay);
        if (_model.Capabilities.PowerOn?.Has(state) != true)
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Not a state a Shelly has after a power cut.");
        }

        if (!State.IsConnected)
        {
            throw new BoardNotConnectedException();
        }

        var target = Endpoint;
        try
        {
            await _poller.RunSwitchesAsync(token => SetPowerOnAndReadAsync(target, relay, state, token), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNoAnswer(ex) || ex is UriFormatException)
        {
            // The poller counted it as a failed poll and reads the device at once, settings included.
            throw new BoardNotConnectedException("The Shelly didn't answer; the setting may not have been saved.", ex);
        }
        catch (Exception ex) when (IsNotTaken(ex) || ex is BoardRefusedException)
        {
            throw new BoardNotConnectedException($"The Shelly didn't take the setting: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Turns off the device's own <paramref name="rules"/>, as a person asked, and reads them back in the same command.
    /// A <see cref="OwnRuleKind.Timer"/> rule turns off, with one <c>Schedule.Update {id, enable:false}</c> each, every
    /// enabled schedule that switches its relay as last read (so the other relays such a schedule switches lose theirs
    /// too, as <see cref="OwnRules.GoesWith(IEnumerable{OwnRule})"/> says); a schedule is never deleted and its calls
    /// never changed. An <see cref="OwnRuleKind.AutoOn"/> rule is one <c>Switch.SetConfig</c> with only
    /// <c>auto_on:false</c> for that switch. A rule given twice is taken once. Returns the rules asked that the device
    /// took, in relay order: a timer once every schedule behind it took its update (also when, as last read, none
    /// switches it any more). Throws <see cref="ArgumentOutOfRangeException"/> for a relay the entry hasn't or another
    /// kind of rule, and <see cref="BoardNotConnectedException"/> without a connection, both before anything is sent;
    /// and <see cref="BoardNotConnectedException"/> when the device took none of them or didn't answer.
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

            if (rule.Kind is not (OwnRuleKind.Timer or OwnRuleKind.AutoOn))
            {
                throw new ArgumentOutOfRangeException(nameof(rules), rule.Kind, "A Shelly's own rules are its schedules and its auto-on.");
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

        var target = Endpoint;
        try
        {
            var taken = new StrongBox<IReadOnlyList<OwnRule>>([]);
            await _poller.RunSwitchesAsync(token => TurnOffRulesAndReadAsync(target, asked, taken, token), cancellationToken).ConfigureAwait(false);
            return taken.Value ?? [];
        }
        catch (Exception ex) when (IsNoAnswer(ex) || ex is UriFormatException)
        {
            // The poller counted it as a failed poll and reads the device at once, rules included.
            throw new BoardNotConnectedException("The Shelly didn't answer; its rules may not have been turned off.", ex);
        }
        catch (Exception ex) when (IsNotTaken(ex) || ex is BoardRefusedException)
        {
            throw new BoardNotConnectedException($"The Shelly didn't turn its rules off: {ex.Message}", ex);
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
    /// Disposes the HTTP client once the poll or command under way has ended (at most the poller's
    /// <see cref="BoardPoller{TReading}.DisposeWait"/>). A command that outlasts the wait throws
    /// <see cref="BoardNotConnectedException"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _poller.DisposeAsync().ConfigureAwait(false);
        Dispose();
    }

    public void Dispose()
    {
        // The poller first, so no poll or command starts on the HTTP client being disposed.
        _poller.Dispose();
        _http.Dispose();
    }

    async Task IPolledBoard<ShellyStatus>.ConnectAsync(CancellationToken cancellationToken)
    {
        // Another device may answer now, or the same one after a reboot: nothing of the last connection's digest is kept.
        _reuseNonce = false;
        _nonce = null;
        _nonceEndpoint = null;
        _nonceCount = 0;

        var target = Endpoint;
        var info = await GetDeviceInfoAsync(target, cancellationToken).ConfigureAwait(false);
        Check(info);
        _reuseNonce = ShellyApi.ParseFirmware(info.Firmware) >= ShellyApi.NonceReuseFirmware;

        _powerOn = null;
        _configRevision = null;
        _configRead = false;
        _configFailed = false;
        _autoOn = null;
        _schedules = null;
        _scheduleRevision = null;
        _schedulesRead = false;
        _schedulesFailed = false;

        var switches = await GetSwitchesAsync(target, routine: false, cancellationToken).ConfigureAwait(false);
        CheckCount(switches);
        var stopped = await ReadPowerOnAsync(target, switches.ConfigRevision, cancellationToken).ConfigureAwait(false);
        await ReadSchedulesAsync(target, switches.ScheduleRevision, skip: stopped, cancellationToken).ConfigureAwait(false);
    }

    async Task<ShellyStatus> IPolledBoard<ShellyStatus>.ReadAsync(CancellationToken cancellationToken)
    {
        return await ReadStatusAsync(Endpoint, readSettings: true, cancellationToken).ConfigureAwait(false);
    }

    void IPolledBoard<ShellyStatus>.OnStateChanged(PolledState<ShellyStatus> state)
    {
        Volatile.Write(ref _lastReported, state.Reading);
        var wasConnected = Interlocked.Exchange(ref _wasConnected, state.IsConnected ? 1 : 0) == 1;
        if (state.IsConnected && !wasConnected)
        {
            var endpoint = Endpoint;
            _logger.Connected(endpoint.Host, endpoint.Port);
        }

        Raise(StateChanged, state);
    }

    // The comparer leaves out the power-on states and the own rules: one of them, or both, changed.
    void IPolledBoard<ShellyStatus>.OnReadingChanged(PolledState<ShellyStatus> state)
    {
        var before = Interlocked.Exchange(ref _lastReported, state.Reading);
        if (before is null || before.PowerOn != state.Reading?.PowerOn)
        {
            Raise(PowerOnChanged, state);
        }

        if (before is null || before.OwnRules != state.Reading?.OwnRules)
        {
            Raise(OwnRulesChanged, state);
        }
    }

    private void Raise(EventHandler<ShellyState>? handlers, PolledState<ShellyStatus> state)
    {
        if (handlers is null)
        {
            return;
        }

        var shellyState = new ShellyState(state.IsConnected, state.Reading, state.LastContactUtc, state.Problem);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<ShellyState>>())
        {
            try
            {
                handler(this, shellyState);
            }
#pragma warning disable CA1031 // A subscriber's bug must not take down the poller or the other subscribers.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.SubscriberFailed(ex);
            }
        }
    }

    void IPolledBoard<ShellyStatus>.OnStep(BoardPollStep kind, string? detail)
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
            case BoardPollStep.Held:
                _logger.Held();
                Trace(BoardTraceDirection.None, "Not trying again until a new password is saved or a person checks again");
                break;
            case BoardPollStep.Stopped:
                ForgetLastStatus();
                Trace(BoardTraceDirection.None, "The app closed the connection");
                break;
            default:
                break;
        }
    }

    // Under the poller's lock.
    private async Task<ShellyDeviceInfo> GetDeviceInfoAsync(BoardEndpoint target, CancellationToken cancellationToken)
    {
        var id = NextId();
        var result = await CallAsync(ShellyJson.WriteRequest(id, ShellyApi.GetDeviceInfo), id, ShellyTraceText.Request(ShellyApi.GetDeviceInfo), routine: false, target, cancellationToken)
            .ConfigureAwait(false);
        var info = ShellyJson.ReadDeviceInfo(result);
        Trace(BoardTraceDirection.Received, ShellyTraceText.DeviceInfo(info));
        return info;
    }

    // Refuses, in plain words, a device that isn't one the app can use: nothing is switched or read on it.
    private void Check(ShellyDeviceInfo info)
    {
        if (info.Generation is not >= 2)
        {
            throw Refuse(
                "The device at this address isn't a Shelly of the second generation or later. A Gen1 Shelly can't be used.",
                ShellyMismatch.NotGen2,
                new ConnectionProblem(ConnectionProblemKind.WrongModel, _model));
        }

        if (string.Equals(info.Profile, "cover", StringComparison.OrdinalIgnoreCase))
        {
            throw Refuse(
                "The Shelly is set up to drive a cover, not relays. Set its profile to \"switch\" in its own web page.",
                ShellyMismatch.CoverProfile,
                new ConnectionProblem(ConnectionProblemKind.CoverProfile, _model));
        }

        if (ShellyApi.ParseFirmware(info.Firmware) is not { } firmware || firmware < ShellyApi.MinFirmware)
        {
            throw Refuse(
                string.Create(CultureInfo.InvariantCulture, $"The Shelly's firmware is too old. Update it to {ShellyApi.MinFirmware.ToString(3)} or later in its own web page."),
                ShellyMismatch.OldFirmware,
                new ConnectionProblem(ConnectionProblemKind.FirmwareTooOld, _model, MinFirmware: ShellyApi.MinFirmware.ToString(3)));
        }
    }

    private void CheckCount(ShellySwitches switches)
    {
        if (switches.Count != _model.Capabilities.RelayCount)
        {
            throw Refuse(
                string.Create(CultureInfo.InvariantCulture, $"The Shelly at this address has {switches.Count} relays, not {_model.Capabilities.RelayCount}."),
                ShellyMismatch.RelayCount,
                new ConnectionProblem(ConnectionProblemKind.WrongModel, _model, ShellyModels.ModelFor(switches.Count), FoundRelayCount: switches.Count));
        }
    }

    private ShellyWrongDeviceException Refuse(string message, ShellyMismatch mismatch, ConnectionProblem? problem)
    {
        Trace(BoardTraceDirection.None, message, isError: true);
        return new ShellyWrongDeviceException(message, mismatch, problem);
    }

    // Under the poller's lock.
    private async Task<ShellySwitches> GetSwitchesAsync(BoardEndpoint target, bool routine, CancellationToken cancellationToken)
    {
        var id = NextId();
        var result = await CallAsync(ShellyJson.WriteRequest(id, ShellyApi.GetStatus), id, ShellyTraceText.Request(ShellyApi.GetStatus), routine, target, cancellationToken)
            .ConfigureAwait(false);
        return ShellyJson.ReadSwitches(result);
    }

    // Under the poller's lock: a poll or the read after a SetConfig (readSettings), or the read that confirms a
    // Switch.Set, which never reads the settings: it carries the last ones, so a group's next step isn't held up.
    private async Task<ShellyStatus> ReadStatusAsync(BoardEndpoint target, bool readSettings, CancellationToken cancellationToken)
    {
        var switches = await GetSwitchesAsync(target, routine: true, cancellationToken).ConfigureAwait(false);

        // The profile can change while the app runs: a device with other switches now is lost, and the connect after
        // it says why.
        CheckCount(switches);

        // The settings again only when they may have changed: not read yet on this connection (a read cancelled, or a
        // SetConfig), another cfg_rev, or SettingsInterval since the last read while cfg_rev can't tell.
        var stopped = false;
        if (readSettings
            && (!_configRead
                || switches.ConfigRevision != _configRevision
                || ((switches.ConfigRevision is null || _configFailed) && _timeProvider.GetUtcNow() - _configReadUtc >= SettingsInterval)))
        {
            stopped = await ReadPowerOnAsync(target, switches.ConfigRevision, cancellationToken).ConfigureAwait(false);
        }

        // The schedules the same way, by schedule_rev.
        if (readSettings
            && (!_schedulesRead
                || switches.ScheduleRevision != _scheduleRevision
                || ((switches.ScheduleRevision is null || _schedulesFailed) && _timeProvider.GetUtcNow() - _schedulesReadUtc >= SettingsInterval)))
        {
            await ReadSchedulesAsync(target, switches.ScheduleRevision, skip: stopped, cancellationToken).ConfigureAwait(false);
        }

        var status = new ShellyStatus((byte)switches.Outputs, _powerOn, CurrentOwnRules());
        var routine = Interlocked.Exchange(ref _lastTraced, status) == status;
        Trace(BoardTraceDirection.Received, ShellyTraceText.Status(status, _model.Capabilities.RelayCount), isRoutine: routine);
        return status;
    }

    // Under the poller's lock: each switch's initial_state, one Switch.GetConfig each, after the status that gave
    // configRevision, so a change in between shows as another cfg_rev at the next poll. Never fails the connect or the
    // poll: only Shelly.GetStatus decides whether the device is there, so a broken settings read can never keep the app
    // from turning a relay off. A switch the device refuses, or whose answer can't be read or names a value the docs
    // don't list, is unknown, and the others are still read. No answer, a password refused or a 429 stops the read:
    // that switch and the ones after it are unknown. Either way the read is tried again after SettingsInterval, or at
    // the next cfg_rev. Only a cancelled poll (a command takes its place, or the app stops) ends it without a result.
    // Each switch's auto_on comes from the same answer: unknown for a switch whose settings couldn't be read. Returns
    // whether the read stopped for no answer, a password refused or a 429.
    private async Task<bool> ReadPowerOnAsync(BoardEndpoint target, long? configRevision, CancellationToken cancellationToken)
    {
        _configRead = false;
        var failed = false;
        var stopped = false;
        var states = new PowerOnState?[_model.Capabilities.RelayCount];
        var autoOn = new bool?[states.Length];
        for (var relay = 1; relay <= states.Length; relay++)
        {
            var before = _powerOn?.For(relay);
            var autoOnBefore = _autoOn?[relay - 1];
            try
            {
                var id = NextId();
                var result = await CallAsync(ShellyJson.WriteSwitchGetConfig(id, relay), id, ShellyTraceText.Request(ShellyApi.SwitchGetConfig, relay), routine: true, target, cancellationToken)
                    .ConfigureAwait(false);
                states[relay - 1] = ShellyJson.ReadPowerOn(result, relay);
                autoOn[relay - 1] = ShellyJson.ReadAutoOn(result);
            }
            catch (Exception ex) when (ex is ShellyRpcException or ShellyProtocolException)
            {
                // The device answered (a refusal is traced by CallAsync), so the next switch is asked. A toast only the
                // first time: the read is tried again every SettingsInterval.
                failed = true;
                if (ex is ShellyProtocolException)
                {
                    Trace(BoardTraceDirection.Received, string.Create(CultureInfo.InvariantCulture, $"Relay {relay}'s settings can't be read: {ex.Message}"), isError: true, isRoutine: _powerOn is not null && before is null);
                }
            }
            catch (Exception ex) when (IsNoAnswer(ex) || IsNotTaken(ex) || ex is UriFormatException)
            {
                // Traced by CallAsync or PostAsync. Asking the next switches would only wait or count toward the
                // device's brute-force protection.
                failed = true;
                stopped = true;
                Trace(BoardTraceDirection.None, ShellyTraceText.PowerOnReadStopped(relay), isError: true);
                break;
            }

            // A toast only when it differs from the last read.
            Trace(
                BoardTraceDirection.Received,
                ShellyTraceText.SettingsRead(relay, states[relay - 1], autoOn[relay - 1]),
                isRoutine: _powerOn is not null && states[relay - 1] == before && autoOn[relay - 1] == autoOnBefore);
        }

        _powerOn = new PowerOnStates(states);
        _autoOn = autoOn;
        _configRevision = configRevision;
        _configFailed = failed || Array.Exists(autoOn, a => a is null);
        _configReadUtc = _timeProvider.GetUtcNow();
        _configRead = true;
        return stopped;
    }

    // Under the poller's lock: one Schedule.List, after the status that gave scheduleRevision. Never fails the connect
    // or the poll, as the settings read: a refusal, an answer that can't be read, no answer, a password refused or a 429
    // makes the schedules unknown, and the read is tried again after SettingsInterval, or at the next schedule_rev.
    // With skip, the settings read just before stopped for one of the last three: nothing is sent, and it counts as
    // failed. Only a cancelled poll ends it without a result.
    private async Task ReadSchedulesAsync(BoardEndpoint target, long? scheduleRevision, bool skip, CancellationToken cancellationToken)
    {
        _schedulesRead = false;
        var before = _schedules;
        ShellySchedules? schedules = null;
        if (skip)
        {
            Trace(BoardTraceDirection.None, ShellyTraceText.SchedulesNotRead("the settings read before it stopped"), isError: true, isRoutine: _schedulesFailed);
        }
        else
        {
            try
            {
                var id = NextId();
                var result = await CallAsync(ShellyJson.WriteRequest(id, ShellyApi.ScheduleList), id, ShellyTraceText.Request(ShellyApi.ScheduleList), routine: true, target, cancellationToken)
                    .ConfigureAwait(false);
                schedules = ShellyJson.ReadSchedules(result, _model.Capabilities.RelayCount);
                Trace(BoardTraceDirection.Received, ShellyTraceText.Schedules(schedules, _model.Capabilities.RelayCount), isRoutine: before is not null && Same(before, schedules));
            }
            catch (Exception ex) when (ex is ShellyRpcException or ShellyProtocolException || IsNoAnswer(ex) || IsNotTaken(ex) || ex is UriFormatException)
            {
                // A refusal, no answer or a 429 is traced by CallAsync or PostAsync; this says what it means. A toast
                // only the first time: the read is tried again every SettingsInterval.
                Trace(BoardTraceDirection.None, ShellyTraceText.SchedulesNotRead(ex.Message), isError: true, isRoutine: _schedulesFailed);
            }
        }

        _schedules = schedules;
        _scheduleRevision = scheduleRevision;
        _schedulesFailed = schedules is null;
        _schedulesReadUtc = _timeProvider.GetUtcNow();
        _schedulesRead = true;

        // What the rules are made of: an enabled schedule's relays. Its id or rev alone doesn't make a new toast.
        static bool Same(ShellySchedules a, ShellySchedules b)
        {
            return a.Jobs.Where(j => j.Enabled && j.Relays != 0).Select(j => j.Relays)
                .SequenceEqual(b.Jobs.Where(j => j.Enabled && j.Relays != 0).Select(j => j.Relays));
        }
    }

    // Under the poller's lock: the own rules as last read, or null while the schedules or a switch's auto_on is unknown.
    // A Timer rule on each relay an enabled schedule switches, one unit per such schedule (its relays' timers), and an
    // AutoOn rule on each relay whose auto_on is on, its own unit; in relay order, the timer first.
    private OwnRules? CurrentOwnRules()
    {
        if (_schedules is not { } schedules || _autoOn is not { } autoOn || Array.Exists(autoOn, a => a is null))
        {
            return null;
        }

        List<OwnRule> rules = [];
        for (var relay = 1; relay <= autoOn.Length; relay++)
        {
            if ((schedules.Relays & (1 << (relay - 1))) != 0)
            {
                rules.Add(new OwnRule(relay, OwnRuleKind.Timer));
            }

            if (autoOn[relay - 1] == true)
            {
                rules.Add(new OwnRule(relay, OwnRuleKind.AutoOn));
            }
        }

        if (rules.Count == 0)
        {
            return Contactor.Boards.OwnRules.Empty;
        }

        List<IReadOnlyCollection<OwnRule>> units = [];
        foreach (var job in schedules.Jobs.Where(j => j.Enabled && j.Relays != 0))
        {
            units.Add([.. new ChannelMask(job.Relays).Channels.Select(r => new OwnRule(r, OwnRuleKind.Timer))]);
        }

        units.AddRange(rules.Where(r => r.Kind == OwnRuleKind.AutoOn).Select(r => (IReadOnlyCollection<OwnRule>)[r]));
        return new OwnRules(rules, units);
    }

    // Under the poller's lock: one Switch.SetConfig, then the status and every switch's settings read back.
    private async Task<CommandReading<ShellyStatus>> SetPowerOnAndReadAsync(BoardEndpoint target, int relay, PowerOnState state, CancellationToken cancellationToken)
    {
        // Another device may be at a new address, unchecked: nothing goes once the address moved or the device was lost.
        EnsureSameDevice(target);

        // Whatever comes of it, the next read reads the settings again, also when cfg_rev didn't move.
        _configRead = false;
        try
        {
            var id = NextId();
            await CallAsync(ShellyJson.WriteSwitchSetPowerOn(id, relay, state), id, ShellyTraceText.SetPowerOn(relay, state), routine: false, target, cancellationToken)
                .ConfigureAwait(false);
            Trace(BoardTraceDirection.Received, ShellyTraceText.SettingSaved(relay));
        }
        catch (ShellyRpcException ex)
        {
            // Refused with the device's own message: an answer, which doesn't count toward losing it.
            throw new BoardRefusedException(string.Create(CultureInfo.InvariantCulture, $"relay {relay}: {(object?)ex.Error ?? ex.Message}"));
        }

        if (Endpoint != target)
        {
            return default;
        }

        try
        {
            return new CommandReading<ShellyStatus>(await ReadStatusAsync(target, readSettings: true, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (IsNotTaken(ex) || IsNoAnswer(ex))
        {
            // The setting went out; the poller keeps the last state and reads again at once, settings included.
            return default;
        }
    }

    // Under the poller's lock: one Schedule.Update per schedule behind the timers asked, one Switch.SetConfig per auto-on
    // asked, then the status, the settings and the schedules read back. Puts the rules the device took in taken.
    private async Task<CommandReading<ShellyStatus>> TurnOffRulesAndReadAsync(BoardEndpoint target, IReadOnlyList<OwnRule> asked, StrongBox<IReadOnlyList<OwnRule>> taken, CancellationToken cancellationToken)
    {
        // Another device may be at a new address, unchecked: nothing goes once the address moved or the device was lost.
        EnsureSameDevice(target);

        var timers = asked.Where(r => r.Kind == OwnRuleKind.Timer).ToList();
        var autoOn = asked.Where(r => r.Kind == OwnRuleKind.AutoOn).ToList();

        // Whatever comes of it, the next read reads what was asked about again, also when a rev didn't move.
        if (timers.Count > 0)
        {
            _schedulesRead = false;
        }

        if (autoOn.Count > 0)
        {
            _configRead = false;
        }

        // The schedules as last read, which the person saw: the ones behind the timers asked, each once.
        var schedules = _schedules;
        List<ShellyJob> jobs = schedules is null ? [] : [.. timers.SelectMany(r => schedules.Switching(r.Relay)).Distinct()];

        HashSet<long> off = [];
        string? refusal = null;
        ExceptionDispatchInfo? unreadable = null;
        var sent = false;
        foreach (var job in jobs)
        {
            EnsureSameDevice(target);
            sent = true;
            try
            {
                var id = NextId();
                await CallAsync(ShellyJson.WriteScheduleOff(id, job.Id), id, ShellyTraceText.TurnOffSchedule(job.Id), routine: false, target, cancellationToken)
                    .ConfigureAwait(false);
                Trace(BoardTraceDirection.Received, ShellyTraceText.ScheduleOff(job.Id));
                off.Add(job.Id);
            }
            catch (Exception ex) when (ex is ShellyRpcException or ShellyProtocolException)
            {
                Refused(string.Create(CultureInfo.InvariantCulture, $"schedule {job.Id}"), ex, ref refusal, ref unreadable);
            }
        }

        List<OwnRule> done = [.. timers.Where(r => schedules is not null && schedules.Switching(r.Relay).All(j => off.Contains(j.Id)))];
        foreach (var rule in autoOn)
        {
            EnsureSameDevice(target);
            sent = true;
            try
            {
                var id = NextId();
                await CallAsync(ShellyJson.WriteSwitchAutoOnOff(id, rule.Relay), id, ShellyTraceText.TurnOffAutoOn(rule.Relay), routine: false, target, cancellationToken)
                    .ConfigureAwait(false);
                Trace(BoardTraceDirection.Received, ShellyTraceText.AutoOnOff(rule.Relay));
                done.Add(rule);
            }
            catch (Exception ex) when (ex is ShellyRpcException or ShellyProtocolException)
            {
                Refused(string.Create(CultureInfo.InvariantCulture, $"relay {rule.Relay}'s auto-on"), ex, ref refusal, ref unreadable);
            }
        }

        if (done.Count == 0)
        {
            if (!sent)
            {
                // Nothing went out: the schedules weren't known. Not an answer, and not a failure of the device.
                throw new BoardNotConnectedException("The Shelly's schedules aren't known yet; nothing was sent.");
            }

            unreadable?.Throw();
            throw new BoardRefusedException(refusal!);
        }

        taken.Value = [.. done.OrderBy(r => r.Relay).ThenBy(r => r.Kind)];
        if (Endpoint != target)
        {
            return default;
        }

        try
        {
            return new CommandReading<ShellyStatus>(await ReadStatusAsync(target, readSettings: true, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (IsNotTaken(ex) || IsNoAnswer(ex))
        {
            // The changes went out; the poller keeps the last state and reads again at once, rules included.
            return default;
        }

        void Refused(string what, Exception ex, ref string? first, ref ExceptionDispatchInfo? broken)
        {
            // Refused with the device's own message, or an answer that can't be read: the change may or may not have
            // been made. The others still go; the read after them shows the truth.
            var message = ex is ShellyRpcException { Error: { } error } ? $"{what}: {error}" : $"{what}: {ex.Message}";
            first ??= message;
            if (ex is not ShellyRpcException)
            {
                broken ??= ExceptionDispatchInfo.Capture(ex);
            }

            Trace(BoardTraceDirection.None, $"The Shelly didn't take the change for {message}", isError: true);
        }
    }

    private async Task<byte> CommandAsync(byte mask, bool on, CancellationToken cancellationToken)
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

        // A poll under way gives way. The device switches one relay per Switch.Set, and the whole mask goes out, as one
        // command on a board that takes a mask would: a group's interlock and "All off" rely on every OFF being sent.
        // The address the whole command goes to: one that moves while it runs gets nothing more (EnsureSameDevice).
        var target = Endpoint;
        try
        {
            var taken = new StrongBox<byte>();
            await _poller.RunSwitchesAsync(token => SwitchEachAsync(target, mask, on, taken, token), cancellationToken).ConfigureAwait(false);
            return taken.Value;
        }
        catch (Exception ex) when (IsNoAnswer(ex) || ex is UriFormatException)
        {
            // The poller counted it as a failed poll and reads the device at once, which shows what it did.
            throw new BoardNotConnectedException("The Shelly didn't answer; the command may not have been carried out.", ex);
        }
        catch (Exception ex) when (IsNotTaken(ex) || ex is BoardRefusedException)
        {
            // No relay was taken. The poller read the device at once, and counted it as a failed poll unless the device
            // refused every relay with an error of its own (BoardRefusedException): that is an answer.
            throw new BoardNotConnectedException($"The Shelly didn't take the command: {ex.Message}", ex);
        }
    }

    // Under the poller's lock. Returns the device's state read after the switches, or none when that read failed, and
    // puts the relays the device took in <paramref name="taken"/>.
    private async Task<CommandReading<ShellyStatus>> SwitchEachAsync(BoardEndpoint target, byte mask, bool on, StrongBox<byte> taken, CancellationToken cancellationToken)
    {
        if (!await SwitchAsync(target, mask, on, taken, cancellationToken).ConfigureAwait(false))
        {
            // Stopped after some relays were taken: the read would meet the same. The poller reads at once instead.
            return default;
        }

        if (Endpoint != target)
        {
            // Moved after the last switch: the device at the new address says nothing about this command.
            return default;
        }

        try
        {
            return new CommandReading<ShellyStatus>(await ReadStatusAsync(target, readSettings: false, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (IsNotTaken(ex) || IsNoAnswer(ex))
        {
            // Never a state the device didn't give: a group would take a made-up "on" as confirmation. The command
            // doesn't fail (what was taken was sent); the poller keeps the last state and reads again at once.
            return default;
        }
    }

    // Under the poller's lock: one Switch.Set per relay of the mask. Throws when not one was taken. Returns false when
    // it stopped part-way after some were taken (see below), true when every relay of the mask got its request.
    private async Task<bool> SwitchAsync(BoardEndpoint target, byte mask, bool on, StrongBox<byte> taken, CancellationToken cancellationToken)
    {
        string? refusal = null;
        ExceptionDispatchInfo? unreadable = null;
        foreach (var relay in new ChannelMask(mask).Channels)
        {
            // Another device may be at a new address, unchecked: nothing more goes once the address moved or the
            // device was lost, and the command throws, as what the old address took says nothing about the new one.
            EnsureSameDevice(target);

            // No answer stops the rest: the device is gone. A password the device refuses, or a 429, stops the rest
            // too: every other relay would get the same, and each try counts toward the device's brute-force
            // protection. Before any relay was taken, that is thrown as it is, and the poller counts it. After, the
            // command returns the relays taken (the IRelayBoard contract), and the poller reads the device at once,
            // which counts on its own if it fails too.
            try
            {
                var id = NextId();
                var result = await CallAsync(ShellyJson.WriteSwitchSet(id, relay, on), id, ShellyTraceText.Request(ShellyApi.SwitchSet, relay, on), routine: false, target, cancellationToken)
                    .ConfigureAwait(false);
                bool? wasOn = result.TryGetProperty("was_on", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value.GetBoolean()
                    : null;
                Trace(BoardTraceDirection.Received, ShellyTraceText.Switched(relay, on, wasOn));
                taken.Value |= (byte)(1 << (relay - 1));
            }
            catch (Exception ex) when (ex is ShellyRpcException or ShellyProtocolException)
            {
                // Refused with the device's own message (e.g. -109 "Overtemp condition present"), or an answer that
                // can't be read: the relay may or may not have switched. The others still go; the read after them
                // shows the truth.
                var message = ex is ShellyRpcException { Error: { } error }
                    ? string.Create(CultureInfo.InvariantCulture, $"relay {relay}: {error}")
                    : string.Create(CultureInfo.InvariantCulture, $"relay {relay}: {ex.Message}");
                refusal ??= message;
                if (ex is not ShellyRpcException)
                {
                    unreadable ??= ExceptionDispatchInfo.Capture(ex);
                }

                Trace(BoardTraceDirection.None, $"The Shelly didn't take the command for {message}", isError: true);
            }
            catch (Exception ex) when (taken.Value != 0 && IsStop(ex))
            {
                Trace(BoardTraceDirection.None, string.Create(CultureInfo.InvariantCulture, $"Stopped at relay {relay}, nothing more sent: {ex.Message}"), isError: true);
                return false;
            }
        }

        // Not one relay was taken: the command failed. When the device refused every relay with an error frame, it
        // answered: that doesn't count toward losing it. An answer that can't be read does.
        if (taken.Value == 0)
        {
            unreadable?.Throw();
            throw new BoardRefusedException(refusal!);
        }

        return true;
    }

    // What stops the rest of a command's mask at the same address: no answer, a password needed or refused, a 429, or
    // not the device it was.
    private static bool IsStop(Exception exception)
    {
        return IsNoAnswer(exception)
            || exception is UriFormatException or ShellyAccessDeniedException or ShellyThrottledException or ShellyWrongDeviceException;
    }

    /// <summary>
    /// Throws <see cref="BoardNotConnectedException"/> when the address moved from <paramref name="target"/> (another
    /// device may be there now), or when the device counts as lost: nothing more is sent then.
    /// </summary>
    private void EnsureSameDevice(BoardEndpoint target)
    {
        if (Endpoint != target || !_poller.State.IsConnected)
        {
            throw new BoardNotConnectedException("The Shelly's address changed, or it was lost: nothing more was sent.");
        }
    }

    // A request that got no answer at all.
    private static bool IsNoAnswer(Exception exception)
    {
        return exception is TimeoutException or HttpRequestException or IOException;
    }

    // The device answered, but didn't do it, couldn't be read, refused the password, asked to wait or isn't the one.
    private static bool IsNotTaken(Exception exception)
    {
        return exception is ShellyRpcException or ShellyProtocolException or ShellyAccessDeniedException or ShellyThrottledException or ShellyWrongDeviceException;
    }

    private int NextId()
    {
        _nextId = _nextId is <= 0 or >= int.MaxValue ? 1 : _nextId + 1;
        return _nextId;
    }

    /// <summary>
    /// One call: the request, with the digest when the device asks for one, and its result. Throws
    /// <see cref="ShellyRpcException"/> for an error frame, <see cref="ShellyAccessDeniedException"/> for a password
    /// needed or refused, <see cref="ShellyThrottledException"/> for a 429, <see cref="ShellyWrongDeviceException"/> for
    /// a 404 (no <c>/rpc</c>: not a Gen2+ device) and <see cref="ShellyProtocolException"/> for anything else it can't
    /// read. Sends at most two requests.
    /// </summary>
    private async Task<JsonElement> CallAsync(byte[] frame, int id, string text, bool routine, BoardEndpoint target, CancellationToken cancellationToken)
    {
        var answer = await AuthenticatedPostAsync(frame, text, routine, target, cancellationToken).ConfigureAwait(false);
        switch (answer.StatusCode)
        {
            case HttpStatusCode.OK:
                var parsed = ShellyJson.ReadFrame(answer.Body, id);
                if (parsed.Error is { } error)
                {
                    Trace(BoardTraceDirection.Received, $"Refused: {error}", answer.Text, isError: true);
                    throw new ShellyRpcException(error);
                }

                return parsed.Result!.Value;
            case HttpStatusCode.TooManyRequests:
                // After wrong passwords, or with too many clients: the screens say to wait, and to check a new password.
                var throttled = new ShellyThrottledException(new ConnectionProblem(ConnectionProblemKind.AskedToWait, _model));
                Trace(BoardTraceDirection.Received, throttled.Message, answer.Text, isError: true);
                throw throttled;
            case HttpStatusCode.NotFound:
                Trace(BoardTraceDirection.Received, "The device has no /rpc", answer.Text, isError: true);
                throw Refuse(
                    "The device at this address isn't a Shelly of the second generation or later. A Gen1 Shelly can't be used.",
                    ShellyMismatch.NotGen2,
                    new ConnectionProblem(ConnectionProblemKind.WrongModel, _model));
            default:
                // An error frame may come with another status than 200; the docs show it only with 200.
                if (ShellyJson.TryReadError(answer.Body, id) is { } rpcError)
                {
                    Trace(BoardTraceDirection.Received, $"Refused: {rpcError}", answer.Text, isError: true);
                    throw new ShellyRpcException(rpcError);
                }

                var message = string.Create(CultureInfo.InvariantCulture, $"The Shelly answered {(int)answer.StatusCode}.");
                Trace(BoardTraceDirection.Received, message, answer.Text, isError: true);
                throw new ShellyProtocolException(message);
        }
    }

    // The digest dance of "Authentication": a kept nonce first when the firmware takes one (2.0.0 and later), otherwise
    // no password first. A 401 gives a challenge, which the request answers once; a 401 to that is a wrong password.
    private async Task<Answer> AuthenticatedPostAsync(byte[] frame, string text, bool routine, BoardEndpoint target, CancellationToken cancellationToken)
    {
        Answer answer;

        // A kept nonce only ever goes back to the address that gave it.
        if (_reuseNonce && _nonce is { } kept && _nonceEndpoint == target && _nonceCount < NonceLimit)
        {
            _nonceCount++;
            answer = await PostAsync(frame, ShellyDigest.Authorization(kept, Password, _nonceCount, ShellyDigest.NewClientNonce()), text, routine, target, cancellationToken)
                .ConfigureAwait(false);
            if (answer.StatusCode != HttpStatusCode.Unauthorized)
            {
                return answer;
            }

            // Expired, used up or forgotten: the refusal brings the new challenge, which this request answers once.
            _nonce = null;
            Trace(BoardTraceDirection.Received, "The Shelly wants a new nonce", answer.Text, isRoutine: true);
        }
        else
        {
            _nonce = null;
            answer = await PostAsync(frame, authorization: null, text, routine, target, cancellationToken).ConfigureAwait(false);
            if (answer.StatusCode != HttpStatusCode.Unauthorized)
            {
                return answer;
            }
        }

        var password = Password;
        if (ShellyDigest.ParseChallenge(answer.Challenges) is not { } challenge)
        {
            const string unreadable = "The Shelly asks for a password in a way the app can't answer.";
            Trace(BoardTraceDirection.Received, unreadable, answer.Text, isError: true);
            throw new ShellyProtocolException(unreadable);
        }

        if (password.Length == 0)
        {
            const string needed = "The Shelly asks for a password. Enter it on the Settings screen.";
            Trace(BoardTraceDirection.Received, needed, answer.Text, isError: true);
            _logger.AccessDenied();
            throw new ShellyAccessDeniedException(needed, new ConnectionProblem(ConnectionProblemKind.LoginNeeded, _model));
        }

        Trace(BoardTraceDirection.Received, "The Shelly asks for the password", answer.Text, isRoutine: true);
        answer = await PostAsync(frame, ShellyDigest.Authorization(challenge, password, 1, ShellyDigest.NewClientNonce()), text, routine, target, cancellationToken)
            .ConfigureAwait(false);
        if (answer.StatusCode == HttpStatusCode.Unauthorized)
        {
            const string refused = "The Shelly refused the password.";
            Trace(BoardTraceDirection.Received, "Wrong password", answer.Text, isError: true);
            _logger.AccessDenied();
            throw new ShellyAccessDeniedException(refused, new ConnectionProblem(ConnectionProblemKind.LoginRefused, _model));
        }

        if (answer.StatusCode == HttpStatusCode.OK && _reuseNonce)
        {
            _nonce = challenge;
            _nonceEndpoint = target;
            _nonceCount = 1;
        }

        return answer;
    }

    /// <summary>
    /// One POST to the device, with a timeout, and its answer read up to <see cref="ShellyApi.MaxResponseBytes"/>.
    /// Traces the request; the caller traces the answer, which it knows the meaning of.
    /// </summary>
    private async Task<Answer> PostAsync(byte[] frame, string? authorization, string text, bool routine, BoardEndpoint target, CancellationToken cancellationToken)
    {
        Trace(BoardTraceDirection.Sent, text, ShellyTraceText.RequestBytes(frame, authorization is not null), isRoutine: routine);
        try
        {
            return await PostCoreAsync(frame, authorization, target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or ShellyProtocolException or UriFormatException)
        {
            // One failed poll doesn't lose the connection, so it shows here.
            Trace(BoardTraceDirection.None, $"No answer: {ex.Message}", isError: true);
            throw;
        }
    }

    private async Task<Answer> PostCoreAsync(byte[] frame, string? authorization, BoardEndpoint target, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"http://{target}{ShellyApi.RpcPath}"))
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new ByteArrayContent(frame),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.ConnectionClose = true;
            if (authorization is not null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > ShellyApi.MaxResponseBytes)
            {
                throw new ShellyProtocolException($"The Shelly's answer is longer than {ShellyApi.MaxResponseBytes} bytes.");
            }

            var body = await ReadCappedAsync(response.Content, linked.Token).ConfigureAwait(false);
            IReadOnlyList<string> challenges = response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.TryGetValues("WWW-Authenticate", out var values)
                ? [.. values]
                : [];
            return new Answer(response.StatusCode, body, ShellyTraceText.Answer((int)response.StatusCode, Encoding.UTF8.GetString(body)), challenges);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The Shelly did not answer within {RequestTimeout.TotalSeconds} s.");
        }
    }

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[ShellyApi.MaxResponseBytes + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total > ShellyApi.MaxResponseBytes)
            {
                throw new ShellyProtocolException($"The Shelly's answer is longer than {ShellyApi.MaxResponseBytes} bytes.");
            }

            return buffer[..total];
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

    private sealed record Answer(HttpStatusCode StatusCode, byte[] Body, string Text, IReadOnlyList<string> Challenges);
}
