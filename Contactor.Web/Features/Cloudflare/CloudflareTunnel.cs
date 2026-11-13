using Contactor.Web.Features.Access;
using Microsoft.Extensions.Options;

namespace Contactor.Web.Features.Cloudflare;

/// <summary>
/// Cloudflare as the way in: keeps the Cloudflare tunnel running while it is the chosen way in, with the token
/// <see cref="CloudflareSettings"/> has (saved in the setup guide, or <c>Tunnel:Token</c>). Starts cloudflared, and
/// starts it again after a growing wait (1 s, doubling to at most 1 min) whenever it stops, and at once with a new
/// token. Without a token it waits for one. The image has no shell or process manager, so the app
/// looks after cloudflared itself.
/// </summary>
public sealed partial class CloudflareTunnel : IAccessMethod
{
    public static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(1);

    // cloudflared that ran this long counts as having worked: the next wait starts from the first again.
    private static readonly TimeSpan RanLongEnough = TimeSpan.FromMinutes(5);

    private readonly TunnelOptions _options;
    private readonly CloudflareSettings _settings;
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

        var wait = FirstWait;
        while (!cancellationToken.IsCancellationRequested)
        {
            var started = _time.GetUtcNow();
            int? exitCode = null;
            Exception? failure = null;
            try
            {
                LogStarting();
                exitCode = await _process.RunAsync(_options.Path, token, LogOutput, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // cloudflared missing or failing to start must not stop the app; it's logged and tried again.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ex;
            }

            // A run that worked for a while starts the waits over.
            if (_time.GetUtcNow() - started >= RanLongEnough)
            {
                wait = FirstWait;
            }

            if (failure is not null)
            {
                LogFailed(failure, wait.TotalSeconds);
            }
            else
            {
                LogStopped(exitCode ?? -1, wait.TotalSeconds);
            }

            try
            {
                await Task.Delay(wait, _time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, LongestWait.Ticks));
        }
    }

    // cloudflared's own lines: they say whether the tunnel is up. Its errors (" ERR ") as warnings. It never writes the
    // token.
    private void LogOutput(string line)
    {
        if (line.Contains(" ERR ", StringComparison.Ordinal))
        {
            LogCloudflaredError(line);
        }
        else
        {
            LogCloudflared(line);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The tunnel's token changed: starting cloudflared again with it")]
    private partial void LogTokenChanged();

    [LoggerMessage(Level = LogLevel.Information, Message = "No tunnel token (Tunnel__Token): cloudflared isn't started")]
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
