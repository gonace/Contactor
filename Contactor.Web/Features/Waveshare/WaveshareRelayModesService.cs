using System.Text.Json;
using System.Threading.Channels;
using Contactor.Boards.Waveshare;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Waveshare;

/// <summary>
/// Logs each time <see cref="WaveshareClient"/> set the board's relays to normal mode on connecting
/// (<c>Waveshare:ResetRelayModes</c>), as Actor <c>system</c>, with the modes it found (<c>"unknown"</c> when it couldn't
/// read them); and, in red for everyone, each connection where the board didn't take it, so its inputs may switch
/// relays past the app. The client does the reset itself, before its first poll; this only records it.
/// </summary>
public sealed partial class WaveshareRelayModesService : BackgroundService
{
    private readonly WaveshareClient _client;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WaveshareRelayModesService> _logger;

    // The client raises the event on its own loop, which must not wait for the database.
    private readonly Channel<object> _resets = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });

    public WaveshareRelayModesService(
        WaveshareClient client,
        IDbContextFactory<ContactorDbContext> dbFactory,
        TimeProvider timeProvider,
        ILogger<WaveshareRelayModesService> logger)
    {
        _client = client;
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    // Subscribes here rather than in ExecuteAsync, which .NET runs on a background thread: the board could
    // connect, and the client reset the modes, before this listens.
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _client.RelayModesReset += OnRelayModesReset;
        _client.RelayModesNotReset += OnRelayModesNotReset;
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var reset in _resets.Reader.ReadAllAsync(stoppingToken))
            {
                await LogAsync(reset, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            _client.RelayModesReset -= OnRelayModesReset;
            _client.RelayModesNotReset -= OnRelayModesNotReset;
        }
    }

    private void OnRelayModesReset(object? sender, WaveshareRelayModesReset reset)
    {
        _resets.Writer.TryWrite(reset);
    }

    private void OnRelayModesNotReset(object? sender, WaveshareRelayModesNotReset notReset)
    {
        _resets.Writer.TryWrite(notReset);
    }

    private async Task LogAsync(object outcome, CancellationToken cancellationToken)
    {
        var (action, details) = outcome switch
        {
            WaveshareRelayModesReset { Modes: { } modes } => (EventActions.BoardRelayModesReset, JsonSerializer.Serialize(new { modes })),
            WaveshareRelayModesReset => (EventActions.BoardRelayModesReset, JsonSerializer.Serialize(new { modes = "unknown" })),
            WaveshareRelayModesNotReset notReset => (EventActions.BoardRelayModesNotReset, JsonSerializer.Serialize(new { reason = notReset.Reason })),
            _ => throw new ArgumentException("Not a relay modes outcome.", nameof(outcome)),
        };

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            db.EventLog.Add(new EventLogEntry
            {
                TimestampUtc = _timeProvider.GetUtcNow().UtcDateTime,
                BoardId = await db.FirstBoardIdAsync(cancellationToken),
                Actor = EventActors.System,
                Action = action,
                Details = details,
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A failure here must not stop the service; it is logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not log what became of the Waveshare board's relay modes")]
    private partial void LogFailed(Exception exception);
}
