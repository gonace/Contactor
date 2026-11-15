using Microsoft.EntityFrameworkCore;
using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Events;

/// <summary>How many events a cleanup removed, and in how many batches.</summary>
public sealed record EventCleanup(int Removed, int Batches);

/// <summary>
/// Keeps the event log to one year: removes the events older than that, run each night right after the backup, so the
/// nightly copies still hold them for a while.
/// </summary>
public sealed partial class EventRetention
{
    /// <summary>How many events one statement removes: each batch is a short transaction of its own.</summary>
    public const int BatchSize = 500;

    /// <summary>The wait between two full batches, so the app's own writes get the database in between.</summary>
    public static readonly TimeSpan PauseBetweenBatches = TimeSpan.FromMilliseconds(50);

    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EventRetention> _logger;

    public EventRetention(IDbContextFactory<ContactorDbContext> dbFactory, TimeProvider timeProvider, ILogger<EventRetention> logger)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// The oldest time an event is kept from: one year before now, in UTC. On 29 February that is 28 February of the
    /// year before (<see cref="DateTime.AddYears"/>), so that day keeps one day more than a year.
    /// </summary>
    public static DateTime CutoffUtc(DateTime nowUtc)
    {
        return nowUtc.AddYears(-1);
    }

    /// <summary>
    /// Removes the events from before <see cref="CutoffUtc"/>, in batches. Kept also when older: the newest event that
    /// the app reads back as "the last" (an input's newest change, a relay's newest off or pulse, the newest end of an
    /// update), so an input's state, "Off since …" and an update's event stay as they were.
    /// </summary>
    public async Task<EventCleanup> RemoveOldAsync(CancellationToken cancellationToken)
    {
        var cutoff = CutoffUtc(_timeProvider.GetUtcNow().UtcDateTime);
        var kept = await LatestIdsAsync(cutoff, cancellationToken);
        var removed = 0;
        var batches = 0;
        while (true)
        {
            // One statement per batch: SQLite holds its write lock only that long (Issue #213).
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var count = await db.EventLog
                .Where(e => e.TimestampUtc < cutoff && !kept.Contains(e.Id))
                .OrderBy(e => e.TimestampUtc)
                .Take(BatchSize)
                .ExecuteDeleteAsync(cancellationToken);
            batches++;
            removed += count;
            if (count < BatchSize)
            {
                break;
            }

            await Task.Delay(PauseBetweenBatches, _timeProvider, cancellationToken);
        }

        LogRemoved(removed, batches, cutoff);
        return new EventCleanup(removed, batches);
    }

    // The old events the app still reads as the latest of their kind. Few: one per input and relay, and one update.
    private async Task<List<int>> LatestIdsAsync(DateTime cutoff, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var old = db.EventLog.AsNoTracking().Where(e => e.TimestampUtc < cutoff);

        // BoardRecorder and InputsHandler: an input's state and time, from its newest change.
        var inputs = await db.EventLog.AsNoTracking()
            .Where(e => e.Action == EventActions.InputChanged && e.InputId != null)
            .GroupBy(e => e.InputId)
            .Select(g => g.Max(e => e.Id))
            .ToListAsync(cancellationToken);

        // Relays: "Off since …", from a relay's newest off or pulse.
        var relays = await db.EventLog.AsNoTracking()
            .Where(e => (e.Action == EventActions.Off || e.Action == EventActions.Pulse) && e.RelayId != null)
            .GroupBy(e => e.RelayId)
            .Select(g => g.Max(e => e.Id))
            .ToListAsync(cancellationToken);

        // UpdateService: the helper's status names the last update, whose event (the last one logged) must stay so it
        // isn't logged again.
        string[] updateEnds = [EventActions.UpdateDone, EventActions.UpdateWentBack, EventActions.UpdateFailed, EventActions.UpdateCurrent];
        var update = await db.EventLog.AsNoTracking()
            .Where(e => updateEnds.Contains(e.Action))
            .MaxAsync(e => (int?)e.Id, cancellationToken);

        var latest = inputs.Concat(relays).ToHashSet();
        if (update is { } updateId)
        {
            latest.Add(updateId);
        }

        // Only the old ones matter: a newer event isn't removed anyway.
        return await old.Where(e => latest.Contains(e.Id)).Select(e => e.Id).ToListAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} events from before {CutoffUtc:u} in {Batches} batches")]
    private partial void LogRemoved(int count, int batches, DateTime cutoffUtc);
}
