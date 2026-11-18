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
/// at connect, and again only when <c>sys.cfg_rev</c> in a status differs from the one it was read with: never on every
/// poll. <see cref="SetPowerOnAsync"/> sets it for one switch with <c>Switch.SetConfig</c> and reads it back at once.
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
    // with, and whether they are read for the current connection. ConnectAsync and a SetConfig start them afresh.
    private PowerOnStates? _powerOn;
    private long? _configRevision;
    private bool _configRead;

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

        var switches = await GetSwitchesAsync(target, routine: false, cancellationToken).ConfigureAwait(false);
        CheckCount(switches);
        await ReadPowerOnAsync(target, switches.ConfigRevision, cancellationToken).ConfigureAwait(false);
    }

    async Task<ShellyStatus> IPolledBoard<ShellyStatus>.ReadAsync(CancellationToken cancellationToken)
    {
        return await ReadStatusAsync(Endpoint, cancellationToken).ConfigureAwait(false);
    }

    void IPolledBoard<ShellyStatus>.OnStateChanged(PolledState<ShellyStatus> state)
    {
        var wasConnected = Interlocked.Exchange(ref _wasConnected, state.IsConnected ? 1 : 0) == 1;
        if (state.IsConnected && !wasConnected)
        {
            var endpoint = Endpoint;
            _logger.Connected(endpoint.Host, endpoint.Port);
        }

        Raise(StateChanged, state);
    }

    // The comparer leaves out only the power-on states: that is what changed.
    void IPolledBoard<ShellyStatus>.OnReadingChanged(PolledState<ShellyStatus> state)
    {
        Raise(PowerOnChanged, state);
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

    // Under the poller's lock: a poll, or the read after a command.
    private async Task<ShellyStatus> ReadStatusAsync(BoardEndpoint target, CancellationToken cancellationToken)
    {
        var switches = await GetSwitchesAsync(target, routine: true, cancellationToken).ConfigureAwait(false);

        // The profile can change while the app runs: a device with other switches now is lost, and the connect after
        // it says why.
        CheckCount(switches);

        // The settings again only when they may have changed: not read yet on this connection (a read cut short, or a
        // SetConfig), or another cfg_rev. A device without cfg_rev is read at connect only.
        if (!_configRead || switches.ConfigRevision != _configRevision)
        {
            await ReadPowerOnAsync(target, switches.ConfigRevision, cancellationToken).ConfigureAwait(false);
        }

        var status = new ShellyStatus((byte)switches.Outputs, _powerOn);
        var routine = Interlocked.Exchange(ref _lastTraced, status) == status;
        Trace(BoardTraceDirection.Received, ShellyTraceText.Status(status, _model.Capabilities.RelayCount), isRoutine: routine);
        return status;
    }

    // Under the poller's lock: each switch's initial_state, one Switch.GetConfig each, after the status that gave
    // configRevision, so a change in between shows as another cfg_rev at the next poll. A switch the device refuses
    // with an error of its own, or whose value the docs don't list, is unknown; no answer, or one the app can't read,
    // fails the read, and the next read starts again.
    private async Task ReadPowerOnAsync(BoardEndpoint target, long? configRevision, CancellationToken cancellationToken)
    {
        _configRead = false;
        var states = new PowerOnState?[_model.Capabilities.RelayCount];
        for (var relay = 1; relay <= states.Length; relay++)
        {
            try
            {
                var id = NextId();
                var result = await CallAsync(ShellyJson.WriteSwitchGetConfig(id, relay), id, ShellyTraceText.Request(ShellyApi.SwitchGetConfig, relay), routine: false, target, cancellationToken)
                    .ConfigureAwait(false);
                states[relay - 1] = ShellyJson.ReadPowerOn(result, relay);
            }
            catch (ShellyRpcException)
            {
                // Traced as refused by CallAsync.
                states[relay - 1] = null;
            }

            Trace(BoardTraceDirection.Received, ShellyTraceText.PowerOnRead(relay, states[relay - 1]));
        }

        _powerOn = new PowerOnStates(states);
        _configRevision = configRevision;
        _configRead = true;
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
            return new CommandReading<ShellyStatus>(await ReadStatusAsync(target, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (IsNotTaken(ex) || IsNoAnswer(ex))
        {
            // The setting went out; the poller keeps the last state and reads again at once, settings included.
            return default;
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
        await SwitchAsync(target, mask, on, taken, cancellationToken).ConfigureAwait(false);
        if (Endpoint != target)
        {
            // Moved after the last switch: the device at the new address says nothing about this command.
            return default;
        }

        try
        {
            return new CommandReading<ShellyStatus>(await ReadStatusAsync(target, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (IsNotTaken(ex) || IsNoAnswer(ex))
        {
            // Never a state the device didn't give: a group would take a made-up "on" as confirmation. The command
            // doesn't fail (what was taken was sent); the poller keeps the last state and reads again at once.
            return default;
        }
    }

    // Under the poller's lock: one Switch.Set per relay of the mask. Throws when not one was taken.
    private async Task SwitchAsync(BoardEndpoint target, byte mask, bool on, StrongBox<byte> taken, CancellationToken cancellationToken)
    {
        string? refusal = null;
        ExceptionDispatchInfo? unreadable = null;
        foreach (var relay in new ChannelMask(mask).Channels)
        {
            // Another device may be at a new address, unchecked: nothing more goes once the address moved or the
            // device was lost.
            EnsureSameDevice(target);

            // No answer is thrown as it is and stops the rest: the device is gone, and the poller counts it. A password
            // the device refuses, or a 429, stops the rest too: every other relay would get the same, and each try
            // counts toward the device's brute-force protection.
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
        }

        // Not one relay was taken: the command failed. When the device refused every relay with an error frame, it
        // answered: that doesn't count toward losing it. An answer that can't be read does.
        if (taken.Value == 0)
        {
            unreadable?.Throw();
            throw new BoardRefusedException(refusal!);
        }
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
        if (_reuseNonce && _nonce is { } kept && _nonceEndpoint == target && _nonceCount < MaxNonceCount)
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
