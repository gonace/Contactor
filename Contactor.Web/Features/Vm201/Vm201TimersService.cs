using System.Text.Json;
using Contactor.Boards;
using Contactor.Boards.Vm201;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Contactor.Web.Features.Vm201;

/// <summary>
/// On the first successful connection after start, disables the VM201's own timer programs
/// (<c>Vm201:DisableBoardTimers</c>), so they can't fight the app's schedules, and logs that as Actor <c>system</c>.
/// </summary>
public sealed partial class Vm201TimersService : BackgroundService
{
    private readonly Vm201Client _client;
    private readonly Vm201Options _options;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<Vm201TimersService> _logger;
    private int _boardTimersHandled;
    private CancellationToken _stoppingToken;
    private Task _boardTimersTask = Task.CompletedTask;

    public Vm201TimersService(
        Vm201Client client,
        IOptions<Vm201Options> options,
        IDbContextFactory<ContactorDbContext> dbFactory,
        TimeProvider timeProvider,
        ILogger<Vm201TimersService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _options = options.Value;
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    // Subscribes here rather than in ExecuteAsync, which .NET runs on a background thread: BoardHostedService could
    // connect, and the board send its first status, before this listens.
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _client.StateChanged += OnStateChanged;
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        try
        {
            // Listens until the app stops; BoardHostedService runs the connection itself.
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            _client.StateChanged -= OnStateChanged;
            await _boardTimersTask;
        }
    }

    // Runs on the client's read loop, so the work goes to its own task.
    private void OnStateChanged(object? sender, Vm201State state)
    {
        if (!state.IsConnected || !_options.DisableBoardTimers || Interlocked.Exchange(ref _boardTimersHandled, 1) == 1)
        {
            return;
        }

        _boardTimersTask = DisableBoardTimersAsync(_stoppingToken);
    }

    private async Task DisableBoardTimersAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.DisableBoardTimersAsync(Vm201Channel.All, cancellationToken);
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            db.EventLog.Add(new EventLogEntry
            {
                TimestampUtc = _timeProvider.GetUtcNow().UtcDateTime,
                Actor = EventActors.System,
                Action = EventActions.BoardTimersDisabled,
                Details = JsonSerializer.Serialize(new { mask = Vm201Channel.All }),
            });
            await db.SaveChangesAsync(cancellationToken);
            LogBoardTimersDisabled();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping.
        }
        catch (BoardNotConnectedException)
        {
            // Lost the connection before the command went out: try again on the next connection.
            Volatile.Write(ref _boardTimersHandled, 0);
        }
#pragma warning disable CA1031 // A failure here must not stop the board connection; it is logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogBoardTimersFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Disabled the VM201's own timer programs")]
    private partial void LogBoardTimersDisabled();

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not disable the VM201's own timer programs")]
    private partial void LogBoardTimersFailed(Exception exception);
}
