using System.Text.Json;
using Contactor.Boards;
using Contactor.Boards.Vm201;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Contactor.Web.Features.Vm201;

/// <summary>
/// Keeps the board connection open for the app's lifetime. On the first successful connection after start it
/// disables the board's own timer programs (<c>Vm201:DisableBoardTimers</c>), so they can't fight the app's
/// schedules, and logs that as Actor <c>system</c>.
/// </summary>
public sealed partial class Vm201HostedService : BackgroundService
{
    private readonly Vm201Client _client;
    private readonly BoardAddressHandler _address;
    private readonly Vm201Options _options;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<Vm201HostedService> _logger;
    private int _boardTimersHandled;
    private CancellationToken _stoppingToken;
    private Task _boardTimersTask = Task.CompletedTask;

    // BoardTrace is taken only so it exists, and listens, before the first connection.
    public Vm201HostedService(
        Vm201Client client,
        BoardAddressHandler address,
        BoardTrace trace,
        IOptions<Vm201Options> options,
        IDbContextFactory<ContactorDbContext> dbFactory,
        TimeProvider timeProvider,
        ILogger<Vm201HostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(trace);
        _client = client;
        _address = address;
        _options = options.Value;
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        _client.StateChanged += OnStateChanged;
        try
        {
            // The address saved on the Inställningar screen, if any, before the first connection.
            await _address.ApplySavedAsync(stoppingToken);
            await _client.RunAsync(stoppingToken);
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
