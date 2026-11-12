using Microsoft.Extensions.Options;

namespace Contactor.Web.Features.Tunnel;

/// <summary>
/// Keeps the Cloudflare tunnel running for the app's lifetime when <c>Tunnel:Token</c> is set: starts cloudflared,
/// and starts it again after a growing wait (1 s, doubling to at most 1 min) whenever it stops. Without a token it
/// does nothing. The image has no shell or process manager, so the app looks after cloudflared itself.
/// </summary>
public sealed partial class TunnelHostedService : BackgroundService
{
    public static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(1);

    // cloudflared that ran this long counts as having worked: the next wait starts from the first again.
    private static readonly TimeSpan RanLongEnough = TimeSpan.FromMinutes(5);

    private readonly TunnelOptions _options;
    private readonly ICloudflaredProcess _process;
    private readonly TimeProvider _time;
    private readonly ILogger<TunnelHostedService> _logger;

    public TunnelHostedService(IOptions<TunnelOptions> options, ICloudflaredProcess process, TimeProvider time, ILogger<TunnelHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _process = process;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            LogNoTunnel();
            return;
        }

        var wait = FirstWait;
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = _time.GetUtcNow();
            try
            {
                LogStarting();
                var exitCode = await _process.RunAsync(_options.Path, _options.Token.Trim(), LogOutput, stoppingToken);
                LogStopped(exitCode, wait.TotalSeconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // cloudflared missing or failing to start must not stop the app; it's logged and tried again.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFailed(ex, wait.TotalSeconds);
            }

            if (_time.GetUtcNow() - started >= RanLongEnough)
            {
                wait = FirstWait;
            }

            try
            {
                await Task.Delay(wait, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, LongestWait.Ticks));
        }
    }

    // cloudflared's own lines, at Information: they say whether the tunnel is up. It never writes the token.
    private void LogOutput(string line)
    {
        LogCloudflared(line);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No tunnel token (Tunnel__Token): cloudflared isn't started")]
    private partial void LogNoTunnel();

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting cloudflared")]
    private partial void LogStarting();

    [LoggerMessage(Level = LogLevel.Information, Message = "cloudflared: {Line}")]
    private partial void LogCloudflared(string line);

    [LoggerMessage(Level = LogLevel.Warning, Message = "cloudflared stopped with exit code {ExitCode}; starting it again in {Seconds} s")]
    private partial void LogStopped(int exitCode, double seconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not run cloudflared; trying again in {Seconds} s")]
    private partial void LogFailed(Exception exception, double seconds);
}
