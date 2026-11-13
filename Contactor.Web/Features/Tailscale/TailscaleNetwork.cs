using System.Text.Json;
using System.Text.RegularExpressions;
using Contactor.Web.Features.Access;

namespace Contactor.Web.Features.Tailscale;

/// <summary>How the app stands on the Tailscale network, as the setup guide shows it.</summary>
/// <param name="Online">Whether the app is on the Tailscale network, and Tailscale Serve answers for it.</param>
/// <param name="Address">Its address there, e.g. <c>contactor.tail1234.ts.net</c>, once Tailscale knows it.</param>
/// <param name="Problem">What stops it, in plain words, or null.</param>
public sealed record TailscaleStatus(bool Online, string? Address, string? Problem)
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

    public TailscaleNetwork(
        TailscaleSettings settings,
        Microsoft.Extensions.Options.IOptions<TailscaleOptions> options,
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
                await RunWithSettingsAsync(_settings.Current, changed.Token);
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

    // While tailscaled runs: joins the network once, then serves the app, and keeps the status up to date. Never
    // throws: tailscaled stopping ends it.
    private async Task WatchAsync(TailscaleNetworkSettings settings, CancellationToken cancellationToken)
    {
        var loggedIn = false;
        var named = false;
        var served = false;
        string? problem = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var state = await ReadStateAsync(cancellationToken);
                if (state is { BackendState: "NeedsLogin" } && !loggedIn && settings.AuthKey.Length == 0)
                {
                    loggedIn = true;
                    problem = "Tailscale needs an auth key: save one in the setup guide.";
                    LogProblem(problem);
                }
                else if (state is { BackendState: "NeedsLogin" } && !loggedIn)
                {
                    var result = await _process.LogInAsync(settings.AuthKey, settings.HostName, LogInTimeout, cancellationToken);
                    if (result.Succeeded)
                    {
                        (loggedIn, named, problem) = (true, true, null);
                        continue;
                    }

                    // A key Tailscale refused stays refused until a new one is saved; one that timed out is tried again
                    // at the next check.
                    loggedIn = !result.TimedOut;
                    problem = $"Tailscale didn't take the auth key: {FirstLine(result, settings)}";
                    LogProblem(problem);
                }

                if (state is { BackendState: "Running" })
                {
                    if (!named)
                    {
                        named = (await _process.RunAsync(["set", $"--hostname={settings.HostName}"], CommandTimeout, cancellationToken)).Succeeded;
                    }

                    if (!served)
                    {
                        var result = await _process.RunAsync(["serve", "--bg", "--https=443", _options.Target], CommandTimeout, cancellationToken);
                        served = result.Succeeded;
                        problem = served ? null : ServeProblem(result, settings);
                        if (!served)
                        {
                            LogProblem(problem!);
                        }
                    }
                }

                SetStatus(new TailscaleStatus(served && state is { BackendState: "Running" }, state?.Address, problem));
                await Task.Delay(CheckEvery, _time, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // tailscaled stopped, or the app.
        }
#pragma warning disable CA1031 // The watch must never take tailscaled down with it; it's logged, and runs again with tailscaled.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogWatchFailed(ex);
        }
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

    // Tailscale Serve over HTTPS needs HTTPS turned on for the Tailscale network; tailscale then gives a link to do so.
    private static string ServeProblem(TailscaleCommandResult result, TailscaleNetworkSettings settings)
    {
        var link = EnableLink().Match(result.Output);
        if (link.Success)
        {
            return $"Turn on HTTPS for your Tailscale network: open {link.Value} and turn it on. Contactor goes on by itself.";
        }

        return result.TimedOut
            ? "Tailscale Serve didn't answer. Contactor tries again."
            : $"Tailscale Serve didn't start: {FirstLine(result, settings)}";
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Watching Tailscale failed")]
    private partial void LogWatchFailed(Exception exception);
}

/// <summary>tailscaled's state as <c>tailscale status --json</c> gives it.</summary>
/// <param name="BackendState">E.g. "NeedsLogin", "Starting" or "Running".</param>
/// <param name="Address">The app's address on the Tailscale network, or null.</param>
public sealed record TailscaleState(string BackendState, string? Address);
