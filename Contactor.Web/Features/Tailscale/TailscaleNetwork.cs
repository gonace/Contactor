using System.Text.Json;
using System.Text.RegularExpressions;
using Contactor.Web.Features.Access;
using Microsoft.Extensions.Options;

namespace Contactor.Web.Features.Tailscale;

/// <summary>How the app stands on the Tailscale network, as the setup guide shows it.</summary>
/// <param name="Online">Whether the app is on the Tailscale network, and Tailscale Serve answers for it.</param>
/// <param name="Address">Its address there, e.g. <c>contactor.tail1234.ts.net</c>, once Tailscale knows it.</param>
/// <param name="Problem">What stops it, in plain words, or null.</param>
/// <param name="Link">
/// Where to fix the problem, for the guide to show as a link: only Tailscale's page to turn on HTTPS, on
/// <c>https://login.tailscale.com</c>. Null for every other problem.
/// </param>
public sealed record TailscaleStatus(bool Online, string? Address, string? Problem, Uri? Link = null)
{
    public static readonly TailscaleStatus Offline = new(false, null, null);
}

/// <summary>
/// Tailscale as the way in: keeps tailscaled running while it is the chosen way in (<see cref="KeepRunning"/>), joins
/// the Tailscale network with the saved auth key the first time, and serves the app with Tailscale Serve over HTTPS.
/// Only phones and computers on the Tailscale network can open it; nothing is public. Starts again at once with new
/// settings.
/// </summary>
public sealed partial class TailscaleNetwork : IAccessMethod
{
    /// <summary>How often the app asks tailscaled how it stands.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LogInTimeout = TimeSpan.FromSeconds(90);
    private const int ProblemMaxLength = 200;

    private readonly TailscaleSettings _settings;
    private readonly TailscaleOptions _options;
    private readonly ITailscaleProcess _process;
    private readonly TimeProvider _time;
    private readonly ILogger<TailscaleNetwork> _logger;
    private TailscaleStatus _status = TailscaleStatus.Offline;

    // The auth key the last run started with; null before the first.
    private string? _lastAuthKey;

    // A new auth key that must join even though tailscaled is on a network already; null once it has. Kept across
    // restarts of tailscaled, so a key that joined isn't used again (most keys are good for one join only).
    private string? _keyToJoinWith;

    private const string CheckFailed = "Contactor couldn't ask Tailscale how it stands. It tries again.";

    public TailscaleNetwork(
        TailscaleSettings settings,
        IOptions<TailscaleOptions> options,
        ITailscaleProcess process,
        TimeProvider time,
        ILogger<TailscaleNetwork> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _settings = settings;
        _options = options.Value;
        _process = process;
        _time = time;
        _logger = logger;
    }

    /// <summary>How the app stands on the Tailscale network now.</summary>
    public TailscaleStatus Status => Volatile.Read(ref _status);

    /// <summary><see cref="Status"/> changed.</summary>
    public event EventHandler? StatusChanged;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Settings saved meanwhile end this run, and the next starts at once with them.
            using var changed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            void OnChanged(object? sender, EventArgs e)
            {
                try
                {
                    changed.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // That run is over already.
                }
            }

            _settings.Changed += OnChanged;
            try
            {
                // An auth key other than the one this run started with joins again, even on a network already.
                var settings = _settings.Current;
                if (_lastAuthKey is not null && settings.AuthKey.Length > 0 && settings.AuthKey != _lastAuthKey)
                {
                    Volatile.Write(ref _keyToJoinWith, settings.AuthKey);
                }

                _lastAuthKey = settings.AuthKey;
                await RunWithSettingsAsync(settings, changed.Token);
            }
            finally
            {
                _settings.Changed -= OnChanged;
                SetStatus(TailscaleStatus.Offline);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                LogSettingsChanged();
            }
        }
    }

    // Keeps tailscaled running, and beside it joins the network, serves the app and watches how it stands.
    private Task RunWithSettingsAsync(TailscaleNetworkSettings settings, CancellationToken cancellationToken)
    {
        return KeepRunning.RunAsync(
            async run =>
            {
                using var daemon = CancellationTokenSource.CreateLinkedTokenSource(run);
                LogStarting();
                var watching = WatchAsync(settings, daemon.Token);
                try
                {
                    return await _process.RunDaemonAsync(LogDaemon, daemon.Token);
                }
                finally
                {
                    await daemon.CancelAsync();
                    await watching;
                    SetStatus(TailscaleStatus.Offline);
                }
            },
            _time,
            (exitCode, failure, wait) =>
            {
                if (failure is not null)
                {
                    LogFailed(failure, wait.TotalSeconds);
                }
                else
                {
                    LogStopped(exitCode ?? -1, wait.TotalSeconds);
                }
            },
            cancellationToken);
    }

    // While tailscaled runs: joins the network once, then serves the app, and keeps the status up to date. A failed
    // check is logged and tried again at the next; only tailscaled stopping ends it. A new auth key joins again even
    // when tailscaled is on a network already (another one, perhaps).
    private async Task WatchAsync(TailscaleNetworkSettings settings, CancellationToken cancellationToken)
    {
        var watch = new Watch(settings.AuthKey.Length > 0 && Volatile.Read(ref _keyToJoinWith) == settings.AuthKey);
        while (!cancellationToken.IsCancellationRequested)
        {
            TailscaleState? state = null;
            try
            {
                state = await ReadStateAsync(cancellationToken);
                if (watch.Problem == CheckFailed)
                {
                    watch.Problem = null;
                }

                if (await CheckAsync(watch, state, settings, cancellationToken))
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The watch must never take tailscaled down with it; it's logged, and tried again.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogCheckFailed(ex);
                watch.Problem = CheckFailed;
            }

            SetStatus(new TailscaleStatus(watch.Served && state is { BackendState: "Running" }, state?.Address, watch.Problem, watch.Link));
            try
            {
                await Task.Delay(CheckEvery, _time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // One check: joins, names and serves as far as the state allows. True: check again at once (just joined).
    private async Task<bool> CheckAsync(Watch watch, TailscaleState? state, TailscaleNetworkSettings settings, CancellationToken cancellationToken)
    {
        var mustLogIn = !watch.LoggedIn && (state is { BackendState: "NeedsLogin" } || (watch.NewKey && state is not null));
        if (mustLogIn && settings.AuthKey.Length == 0)
        {
            watch.LoggedIn = true;
            watch.Problem = "Tailscale needs an auth key: save one in the setup guide.";
            LogProblem(watch.Problem);
            return false;
        }

        if (mustLogIn)
        {
            var result = await _process.LogInAsync(settings.AuthKey, settings.HostName, force: watch.NewKey, LogInTimeout, cancellationToken);
            // A forced join is done once the key joined, or Tailscale refused it: either way it isn't tried again.
            if (watch.NewKey && !result.TimedOut)
            {
                Interlocked.CompareExchange(ref _keyToJoinWith, null, settings.AuthKey);
            }

            if (result.Succeeded)
            {
                (watch.LoggedIn, watch.Named, watch.Problem) = (true, true, null);
                return true;
            }

            // A key Tailscale refused stays refused until a new one is saved; one that timed out is tried again at
            // the next check.
            watch.LoggedIn = !result.TimedOut;
            watch.Problem = $"Tailscale didn't take the auth key: {FirstLine(result, settings)}";
            LogProblem(watch.Problem);
            return false;
        }

        switch (state?.BackendState)
        {
            case "NeedsMachineAuth":
                watch.Problem = $"Approve {settings.HostName} in Tailscale's admin console, under Machines. Contactor goes on by itself.";
                return false;
            case "Stopped":
                watch.Problem = "Tailscale is stopped on this machine.";
                return false;
            case "Running":
                break;
            default:
                return false;
        }

        if (!watch.Named)
        {
            watch.Named = (await _process.RunAsync(["set", $"--hostname={settings.HostName}"], CommandTimeout, cancellationToken)).Succeeded;
        }

        if (!watch.Served)
        {
            (watch.Problem, watch.Link) = await ServeAsync(settings, cancellationToken);
            watch.Served = watch.Problem is null;
            if (!watch.Served)
            {
                LogProblem(watch.Problem!);
            }
        }

        return false;
    }

    // Has Tailscale Serve pass HTTPS requests to the app's address for it. Null when it does, else what stops it, with
    // Tailscale's page to fix it when there is one. tailscale can exit 0 without serving (HTTPS not turned on), so Serve's
    // own status is the proof.
    private async Task<(string? Problem, Uri? Link)> ServeAsync(TailscaleNetworkSettings settings, CancellationToken cancellationToken)
    {
        if (_options.ServeEndPoint is null)
        {
            return ("Contactor has no address for Tailscale Serve (Tailscale:ServeAddress).", null);
        }

        var result = await _process.RunAsync(["serve", "--bg", "--https=443", _options.Target], CommandTimeout, cancellationToken);
        if (!result.Succeeded || EnableLink().IsMatch(result.Output))
        {
            return ServeProblem(result, settings);
        }

        var status = await _process.RunAsync(["serve", "status", "--json"], CommandTimeout, cancellationToken);
        return status.Succeeded && Regex.IsMatch(status.Output, $"\"Proxy\"\\s*:\\s*\"{Regex.Escape(_options.Target)}\"")
            ? (null, null)
            : ("Tailscale Serve didn't take the app. Contactor tries again.", null);
    }

    // How far the watch got while this tailscaled runs.
    private sealed class Watch
    {
        public Watch(bool newKey)
        {
            NewKey = newKey;
        }

        public bool NewKey { get; }

        public bool LoggedIn { get; set; }

        public bool Named { get; set; }

        public bool Served { get; set; }

        // A new problem drops the link of the one before.
        public string? Problem
        {
            get;
            set
            {
                field = value;
                Link = null;
            }
        }

        // Set after the problem it belongs to.
        public Uri? Link { get; set; }
    }

    // tailscaled's state and the app's address, or null while it doesn't answer (e.g. still starting).
    private async Task<TailscaleState?> ReadStateAsync(CancellationToken cancellationToken)
    {
        var result = await _process.RunAsync(["status", "--json"], CommandTimeout, cancellationToken);
        return result.TimedOut ? null : ParseState(result.Output);
    }

    /// <summary>
    /// What <c>tailscale status --json</c> says: its state (e.g. "NeedsLogin" or "Running") and the app's address, or
    /// null when it isn't that.
    /// </summary>
    public static TailscaleState? ParseState(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        // The JSON may come after a warning line.
        var start = output.IndexOf('{', StringComparison.Ordinal);
        var end = output.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(output.AsMemory(start, end - start + 1));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("BackendState", out var backendState)
                || backendState.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? address = null;
            if (root.TryGetProperty("Self", out var self)
                && self.ValueKind == JsonValueKind.Object
                && self.TryGetProperty("DNSName", out var dnsName)
                && dnsName.ValueKind == JsonValueKind.String)
            {
                // "contactor.tail1234.ts.net." is a host the guide links to: only a plain one is taken.
                var host = dnsName.GetString()!.TrimEnd('.').ToLowerInvariant();
                address = host.Length > 0 && Uri.CheckHostName(host) == UriHostNameType.Dns && host.Contains('.', StringComparison.Ordinal) ? host : null;
            }

            return new TailscaleState(backendState.GetString()!, address);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Tailscale Serve over HTTPS needs HTTPS turned on for the Tailscale network; tailscale then gives a link to do so,
    // which the guide shows as a link of its own.
    private static (string Problem, Uri? Link) ServeProblem(TailscaleCommandResult result, TailscaleNetworkSettings settings)
    {
        if (EnableLink().IsMatch(result.Output))
        {
            return ("Turn on HTTPS for your Tailscale network, with Funnel left off. Contactor goes on by itself.", HttpsLink(result.Output));
        }

        return result.TimedOut
            ? ("Tailscale Serve didn't answer. Contactor tries again.", null)
            : ($"Tailscale Serve didn't start: {FirstLine(result, settings)}", null);
    }

    /// <summary>
    /// The link tailscale gives to turn on HTTPS for the Tailscale network, or null when its output has none. Only an
    /// https address on login.tailscale.com, on its own port, is taken, since the guide links to it.
    /// </summary>
    public static Uri? HttpsLink(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var match = EnableLink().Match(output);
        return match.Success
            && Uri.TryCreate(match.Value, UriKind.Absolute, out var link)
            && link.Scheme == Uri.UriSchemeHttps
            && link.IsDefaultPort
            && link.UserInfo.Length == 0
            && string.Equals(link.Host, "login.tailscale.com", StringComparison.OrdinalIgnoreCase)
            ? link
            : null;
    }

    // The first line tailscale wrote, short, and never with the auth key in it.
    private static string FirstLine(TailscaleCommandResult result, TailscaleNetworkSettings settings)
    {
        if (result.TimedOut)
        {
            return "Tailscale didn't answer in time.";
        }

        var line = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? $"exit code {result.ExitCode}";
        if (settings.AuthKey.Length > 0)
        {
            line = line.Replace(settings.AuthKey, "…", StringComparison.Ordinal);
        }

        return line.Length > ProblemMaxLength ? line[..ProblemMaxLength] + "…" : line;
    }

    private void SetStatus(TailscaleStatus status)
    {
        if (Interlocked.Exchange(ref _status, status) != status)
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [GeneratedRegex(@"https://login\.tailscale\.com/[^\s""'<>]+")]
    private static partial Regex EnableLink();

    // tailscaled writes a great deal: only for looking into a problem.
    [LoggerMessage(Level = LogLevel.Debug, Message = "tailscaled: {Line}")]
    private partial void LogDaemon(string line);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tailscale's settings changed: starting tailscaled again with them")]
    private partial void LogSettingsChanged();

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting tailscaled")]
    private partial void LogStarting();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tailscale: {Problem}")]
    private partial void LogProblem(string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "tailscaled stopped with exit code {ExitCode}; starting it again in {Seconds} s")]
    private partial void LogStopped(int exitCode, double seconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not run tailscaled; trying again in {Seconds} s")]
    private partial void LogFailed(Exception exception, double seconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Asking Tailscale how it stands failed; trying again")]
    private partial void LogCheckFailed(Exception exception);
}

/// <summary>tailscaled's state as <c>tailscale status --json</c> gives it.</summary>
/// <param name="BackendState">E.g. "NeedsLogin", "Starting" or "Running".</param>
/// <param name="Address">The app's address on the Tailscale network, or null.</param>
public sealed record TailscaleState(string BackendState, string? Address);
