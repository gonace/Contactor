using System.Text.Json;
using Contactor.Boards;
using Contactor.Boards.Vm201;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Contactor.Web.Features.Vm201;

/// <summary>
/// On the board's first successful connection, disables the VM201's own timer programs
/// (<c>Vm201:DisableBoardTimers</c>), so they can't fight the app's schedules, and logs that as Actor <c>system</c>. A new
/// address may be another VM201, so its timers are disabled again at its first answer. Runs only while the VM201 is the
/// chosen board: <see cref="Boards.BoardSwitch"/> starts it before the board's connection and stops it with it.
/// </summary>
public sealed partial class Vm201TimersService : BackgroundService
{
    private readonly Vm201Client _client;
    private readonly Vm201Options _options;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<Vm201TimersService> _logger;
    // Its own, cancelled when the service stops: known from before it listens, unlike ExecuteAsync's token, which .NET
    // hands over on a background thread, so a first status can't start work that stopping the service doesn't end.
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationToken _stoppingToken;
    private int _boardTimersHandled;

    // Written on the client's read loop, awaited when the service stops.
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
        _stoppingToken = _stopping.Token;
    }

    // Subscribes here rather than in ExecuteAsync, which .NET runs on a background thread: the board's connection could
    // start, and the board send its first status, before this listens.
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _client.StateChanged += OnStateChanged;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _stopping.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Listens until the board stops being the chosen one, or the app stops; the switch runs the connection.
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            _client.StateChanged -= OnStateChanged;
            await Volatile.Read(ref _boardTimersTask);
        }
    }

    // Runs on the client's read loop, so the work goes to its own task.
    private void OnStateChanged(object? sender, Vm201State state)
    {
        // Forgotten for a new address (the client's SetEndpoint): the board there gets its timers disabled too.
        if (!state.IsConnected && state.LastContactUtc is null)
        {
            Volatile.Write(ref _boardTimersHandled, 0);
            return;
        }

        if (!state.IsConnected || !_options.DisableBoardTimers || _stoppingToken.IsCancellationRequested
            || Interlocked.Exchange(ref _boardTimersHandled, 1) == 1)
        {
            return;
        }

        Volatile.Write(ref _boardTimersTask, DisableBoardTimersAsync(_stoppingToken));
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
