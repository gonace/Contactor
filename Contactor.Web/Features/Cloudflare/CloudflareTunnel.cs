using Contactor.Web.Features.Access;
using Microsoft.Extensions.Options;

namespace Contactor.Web.Features.Cloudflare;

/// <summary>
/// Cloudflare as the way in: keeps the Cloudflare tunnel running while it is the chosen way in, with the token
/// <see cref="CloudflareSettings"/> has (saved in the setup guide, or <c>Tunnel:Token</c>). Starts cloudflared, and
/// starts it again after a growing wait (1 s, doubling to at most 1 min) whenever it stops, and at once with a new
/// token (<see cref="KeepRunning"/>). Without a token it waits for one.
/// </summary>
public sealed partial class CloudflareTunnel : IAccessMethod
{
    private readonly TunnelOptions _options;
    private readonly CloudflareSettings _settings;
    private volatile bool _connected;
    private readonly ICloudflaredProcess _process;
    private readonly TimeProvider _time;
    private readonly ILogger<CloudflareTunnel> _logger;

    public CloudflareTunnel(IOptions<TunnelOptions> options, CloudflareSettings settings, ICloudflaredProcess process, TimeProvider time, ILogger<CloudflareTunnel> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _settings = settings;
        _process = process;
        _time = time;
        _logger = logger;
    }

    /// <summary>Whether cloudflared has reported a connection to Cloudflare since it last started, for the setup guide.</summary>
    public bool Connected => _connected;

    /// <summary><see cref="Connected"/> changed.</summary>
    public event EventHandler? ConnectedChanged;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // A token saved meanwhile ends this run, and the next starts at once with it.
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

            _settings.TunnelChanged += OnChanged;
            try
            {
                await RunWithTokenAsync(_settings.Tunnel.Token, changed.Token);
            }
            finally
            {
                _settings.TunnelChanged -= OnChanged;
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                LogTokenChanged();
            }
        }
    }

    // Keeps cloudflared running with this token until cancelled; without one, waits.
    private async Task RunWithTokenAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            LogNoTunnel();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, _time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Stopping, or a token was saved.
            }

            return;
        }

        await KeepRunning.RunAsync(
            async run =>
            {
                try
                {
                    LogStarting();
                    return await _process.RunAsync(_options.Path, token, LogOutput, run);
                }
                finally
                {
                    SetConnected(false);
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

    // cloudflared's own lines: they say whether the tunnel is up. Its errors (" ERR ") as warnings. It never writes the
    // token.
    private void LogOutput(string line)
    {
        // How cloudflared says a connection to Cloudflare's edge is up.
        if (line.Contains("Registered tunnel connection", StringComparison.Ordinal))
        {
            SetConnected(true);
        }

        if (line.Contains(" ERR ", StringComparison.Ordinal))
        {
            LogCloudflaredError(line);
        }
        else
        {
            LogCloudflared(line);
        }
    }

    private void SetConnected(bool connected)
    {
        if (_connected != connected)
        {
            _connected = connected;
            ConnectedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The tunnel's token changed: starting cloudflared again with it")]
    private partial void LogTokenChanged();

    [LoggerMessage(Level = LogLevel.Information, Message = "No tunnel token yet: cloudflared waits for one")]
    private partial void LogNoTunnel();

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting cloudflared")]
    private partial void LogStarting();

    [LoggerMessage(Level = LogLevel.Information, Message = "cloudflared: {Line}")]
    private partial void LogCloudflared(string line);

    [LoggerMessage(Level = LogLevel.Warning, Message = "cloudflared: {Line}")]
    private partial void LogCloudflaredError(string line);

    [LoggerMessage(Level = LogLevel.Warning, Message = "cloudflared stopped with exit code {ExitCode}; starting it again in {Seconds} s")]
    private partial void LogStopped(int exitCode, double seconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not run cloudflared; trying again in {Seconds} s")]
    private partial void LogFailed(Exception exception, double seconds);
}
