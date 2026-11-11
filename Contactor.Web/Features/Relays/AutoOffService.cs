using System.Text.Json;
using Contactor.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Relays;

/// <summary>
/// Turns relays off when their auto-off time has passed: every 15 s it looks for a <see cref="RelayRuntime"/> whose
/// <see cref="RelayRuntime.AutoOffAtUtc"/> is due, sends OFF if the board says the relay is still on, logs it as
/// Actor <c>auto-off</c>, and clears the runtime. Only a person's ON sets an auto-off time (and a new limit only
/// moves it for such a relay), so relays a schedule turned on are never touched. While the board is disconnected nothing happens; a time that passed meanwhile (also before
/// a restart) is handled at the first check after the connection is back.
/// </summary>
public sealed partial class AutoOffService : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    private readonly IRelayBoard _board;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly LocalClock _clock;
    private readonly ExpectedChanges _expected;
    private readonly RelayChanges _changes;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AutoOffService> _logger;

    public AutoOffService(
        IRelayBoard board,
        IDbContextFactory<ContactorDbContext> dbFactory,
        LocalClock clock,
        ExpectedChanges expected,
        RelayChanges changes,
        TimeProvider timeProvider,
        ILogger<AutoOffService> logger)
    {
        _board = board;
        _dbFactory = dbFactory;
        _clock = clock;
        _expected = expected;
        _changes = changes;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Handles every auto-off that is due now. Returns the relays it turned off.</summary>
    public async Task<IReadOnlyList<int>> CheckAsync(CancellationToken cancellationToken)
    {
        var state = _board.State;
        if (!state.IsConnected)
        {
            return [];
        }

        var now = _clock.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var due = await db.RelayRuntimes
            .Where(r => r.AutoOffAtUtc != null && r.AutoOffAtUtc <= now)
            .OrderBy(r => r.RelayId)
            .ToListAsync(cancellationToken);

        List<int> turnedOff = [];
        foreach (var runtime in due)
        {
            if (state.IsOn(runtime.RelayId))
            {
                var mask = _board.Capabilities.Relay(runtime.RelayId);
                _expected.Expect(mask, on: false);
                try
                {
                    await _board.TurnOffAsync(mask, cancellationToken);
                }
                catch (BoardNotConnectedException)
                {
                    // Lost the board mid-way: the rest waits for the next check.
                    break;
                }

                var minutes = runtime.OnSinceUtc is { } since ? (int)Math.Round((runtime.AutoOffAtUtc!.Value - since).TotalMinutes) : (int?)null;
                db.EventLog.Add(new EventLogEntry
                {
                    TimestampUtc = now,
                    RelayId = runtime.RelayId,
                    Actor = EventActors.AutoOff,
                    Action = EventActions.Off,
                    Details = minutes is null ? null : JsonSerializer.Serialize(new { afterMinutes = minutes }),
                });
                turnedOff.Add(runtime.RelayId);
            }

            // Off already (turned off some other way): nothing to send, but the plan is stale.
            runtime.OnSinceUtc = null;
            runtime.AutoOffAtUtc = null;
            runtime.TurnedOnBy = null;
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(cancellationToken);
            _changes.Notify();
        }

        return turnedOff;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(stoppingToken);
                await Task.Delay(CheckInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // One failed check must not stop auto-off for good; it is logged and retried.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogCheckFailed(ex);
                await Task.Delay(CheckInterval, _timeProvider, stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Auto-off check failed")]
    private partial void LogCheckFailed(Exception exception);
}
