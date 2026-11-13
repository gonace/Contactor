using System.Text.Json;
using System.Threading.Channels;
using Contactor.Boards.Waveshare;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Waveshare;

/// <summary>
/// Logs each time <see cref="WaveshareClient"/> set the board's relays to normal mode on connecting
/// (<c>Waveshare:ResetRelayModes</c>), as Actor <c>system</c>, with the modes it found. The client does the reset itself,
/// before its first poll, so no input switches a relay past the app; this only records it.
/// </summary>
public sealed partial class WaveshareRelayModesService : BackgroundService
{
    private readonly WaveshareClient _client;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WaveshareRelayModesService> _logger;

    // The client raises the event on its own loop, which must not wait for the database.
    private readonly Channel<WaveshareRelayModesReset> _resets = Channel.CreateUnbounded<WaveshareRelayModesReset>(new UnboundedChannelOptions { SingleReader = true });

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

    // Subscribes here rather than in ExecuteAsync, which .NET runs on a background thread: BoardHostedService could
    // connect, and the client reset the modes, before this listens.
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _client.RelayModesReset += OnRelayModesReset;
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
        }
    }

    private void OnRelayModesReset(object? sender, WaveshareRelayModesReset reset)
    {
        _resets.Writer.TryWrite(reset);
    }

    private async Task LogAsync(WaveshareRelayModesReset reset, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            db.EventLog.Add(new EventLogEntry
            {
                TimestampUtc = _timeProvider.GetUtcNow().UtcDateTime,
                Actor = EventActors.System,
                Action = EventActions.BoardRelayModesReset,
                Details = JsonSerializer.Serialize(new { modes = reset.Modes }),
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not log that the Waveshare board's relays were set to normal mode")]
    private partial void LogFailed(Exception exception);
}
