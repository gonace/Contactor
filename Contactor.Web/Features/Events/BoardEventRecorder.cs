using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Contactor.Vm201;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Events;

/// <summary>
/// Logs what happens at the board without the app asking: relays turned on or off through the board's web page,
/// its button or its timers (Actor <c>external</c>), changes to the input, and the connection being lost for more
/// than 30 s and coming back. The board's status arrives on its read loop; it is queued and handled here one at a
/// time, so the database work never holds up the connection.
/// </summary>
public sealed partial class BoardEventRecorder : BackgroundService
{
    public static readonly TimeSpan ConnectionLostAfter = TimeSpan.FromSeconds(30);

    // How often a lasting disconnect is checked for while there is no new status.
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);

    private readonly Vm201Client _client;
    private readonly ExpectedChanges _expected;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly LocalClock _clock;
    private readonly RelayChanges _changes;
    private readonly ILogger<BoardEventRecorder> _logger;
    private readonly Channel<Vm201State> _states = Channel.CreateUnbounded<Vm201State>(new UnboundedChannelOptions { SingleReader = true });

    // The last status the board reported while connected; null until the first one after start.
    private Vm201State? _known;
    private DateTime? _disconnectedSinceUtc;
    private bool _lossLogged;

    public BoardEventRecorder(
        Vm201Client client,
        ExpectedChanges expected,
        IDbContextFactory<ContactorDbContext> dbFactory,
        LocalClock clock,
        RelayChanges changes,
        ILogger<BoardEventRecorder> logger)
    {
        _client = client;
        _expected = expected;
        _dbFactory = dbFactory;
        _clock = clock;
        _changes = changes;
        _logger = logger;
    }

    // Subscribes here rather than in ExecuteAsync, which .NET runs on a background thread: the first status
    // could arrive before it subscribes, and the next change would then be taken as the starting point.
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _client.StateChanged += OnStateChanged;
        var state = _client.State;
        if (state.IsConnected)
        {
            _states.Writer.TryWrite(state);
        }
        else
        {
            // Not connected yet counts as a disconnect from the start, so a board that never answers is logged too.
            _disconnectedSinceUtc = _clock.UtcNow;
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(CheckInterval);
                try
                {
                    while (await _states.Reader.WaitToReadAsync(wait.Token))
                    {
                        while (_states.Reader.TryRead(out var state))
                        {
                            await HandleSafelyAsync(() => HandleAsync(state, stoppingToken));
                        }
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // The check interval passed without a new status.
                }

                await HandleSafelyAsync(() => CheckConnectionLostAsync(stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            _client.StateChanged -= OnStateChanged;
        }
    }

    private void OnStateChanged(object? sender, Vm201State state)
    {
        _states.Writer.TryWrite(state);
    }

    private async Task HandleAsync(Vm201State state, CancellationToken cancellationToken)
    {
        if (!state.IsConnected)
        {
            // Contact was lost after the last status, the same time the warning on the relay screen shows.
            _disconnectedSinceUtc ??= state.LastContactUtc?.UtcDateTime ?? _clock.UtcNow;
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var now = _clock.UtcNow;
        if (_disconnectedSinceUtc is { } since)
        {
            if (_lossLogged)
            {
                db.EventLog.Add(new EventLogEntry
                {
                    TimestampUtc = now,
                    Actor = EventActors.System,
                    Action = EventActions.ConnectionRestored,
                    Details = JsonSerializer.Serialize(new { afterSeconds = (int)(now - since).TotalSeconds }),
                });
            }

            _disconnectedSinceUtc = null;
            _lossLogged = false;
        }

        // The first status after start has nothing to compare with: what happened before is unknown. After a
        // reconnect it is compared with the last status before the loss, so changes made meanwhile are logged.
        if (_known is { } known)
        {
            await RecordRelayChangesAsync(db, known.Outputs, state.Outputs, now, state.LastContactUtc?.UtcDateTime ?? now, cancellationToken);
            if (known.Input != state.Input)
            {
                db.EventLog.Add(new EventLogEntry
                {
                    TimestampUtc = now,
                    Actor = EventActors.External,
                    Action = EventActions.InputChanged,
                    Details = JsonSerializer.Serialize(new { on = state.Input }),
                });
            }
        }

        _known = state;
        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(cancellationToken);
            _changes.Notify();
        }
    }

    private async Task RecordRelayChangesAsync(
        ContactorDbContext db,
        byte before,
        byte after,
        DateTime now,
        DateTime receivedUtc,
        CancellationToken cancellationToken)
    {
        for (var relayId = 1; relayId <= Vm201Channel.Count; relayId++)
        {
            var mask = Vm201Channel.Mask(relayId);
            var wasOn = (before & mask) != 0;
            var isOn = (after & mask) != 0;
            if (wasOn == isOn || _expected.TryConsume(relayId, isOn, receivedUtc))
            {
                continue;
            }

            db.EventLog.Add(new EventLogEntry
            {
                TimestampUtc = now,
                RelayId = relayId,
                Actor = EventActors.External,
                Action = isOn ? EventActions.On : EventActions.Off,
            });

            // Whoever turned it on in the app no longer has it on: the status line must not keep their name, and
            // auto-off must not act on it.
            var runtime = await db.RelayRuntimes.SingleOrDefaultAsync(r => r.RelayId == relayId, cancellationToken);
            if (runtime is not null)
            {
                runtime.OnSinceUtc = null;
                runtime.AutoOffAtUtc = null;
                runtime.TurnedOnBy = null;
            }
        }
    }

    private async Task CheckConnectionLostAsync(CancellationToken cancellationToken)
    {
        if (_disconnectedSinceUtc is not { } since || _lossLogged || _clock.UtcNow - since < ConnectionLostAfter)
        {
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        db.EventLog.Add(new EventLogEntry
        {
            TimestampUtc = since,
            Actor = EventActors.System,
            Action = EventActions.ConnectionLost,
        });
        await db.SaveChangesAsync(cancellationToken);
        _lossLogged = true;
        _changes.Notify();
    }

    private async Task HandleSafelyAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A failed write must not stop the recorder; it is logged and the next status goes on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not record a board event")]
    private partial void LogFailed(Exception exception);
}
