using System.Runtime.CompilerServices;
using Contactor.Web.Features.Events;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Relays;

/// <summary>
/// The one place that writes a relay's run (<see cref="RelayRuntime"/>: who turned it on, since when, and its
/// auto-off). A writer states what it wants on its own database context, so its events save in the same transaction,
/// and saves with <see cref="SaveAsync"/>. A run changed by someone else since it was read
/// (<see cref="RelayRuntime.Version"/>) is read again, the same wishes are applied to what is there now, each by its
/// own rule, and the save is tried again: nothing is written over someone else's change.
/// <list type="bullet">
/// <item>After an ON the one that turned it on owns the run (<see cref="Start"/>).</item>
/// <item>After an OFF only the run that was on when the OFF went out ends (<see cref="End"/>).</item>
/// <item>Without a command, a wish applies only while the run is as it was read (<see cref="StartOutside"/>,
/// <see cref="EndOutside"/>) or as it requires (<see cref="GiveOwnAutoOff"/>, <see cref="TakeOverForSchedule"/>,
/// <see cref="SetAutoOff"/>).</item>
/// </list>
/// </summary>
public static class RelayRuns
{
    /// <summary>How many times a save is tried when other writers keep changing the same runs.</summary>
    public const int MaxTries = 3;

    // The wishes made on each context since its last save, to apply again to what another writer saved meanwhile.
    private static readonly ConditionalWeakTable<ContactorDbContext, List<Wish>> Wishes = [];

    private delegate void Apply(RelayRuntime run);

    /// <summary>
    /// After an ON the board took: <paramref name="owner"/> (an e-mail, <c>input:n</c> or <c>schedule</c>) turned it
    /// on at <paramref name="since"/>, with <paramref name="autoOffAt"/> or none. The whole run is theirs, whatever was
    /// there.
    /// </summary>
    public static Task StartAsync(ContactorDbContext db, int relayId, string owner, DateTime since, DateTime? autoOffAt, CancellationToken cancellationToken)
    {
        void Write(RelayRuntime run)
        {
            run.OnSinceUtc = since;
            run.TurnedOnBy = owner;
            run.AutoOffAtUtc = autoOffAt;
        }

        return WishAsync(db, relayId, Write, Write, cancellationToken);
    }

    /// <summary>
    /// After an OFF that went out at <paramref name="sentUtc"/>: the run ends, unless it began after the OFF went out
    /// (someone turned the relay on again meanwhile, whose run stays).
    /// </summary>
    public static Task EndAsync(ContactorDbContext db, int relayId, DateTime sentUtc, CancellationToken cancellationToken)
    {
        void Write(RelayRuntime run)
        {
            if (run.OnSinceUtc is null || run.OnSinceUtc <= sentUtc)
            {
                Clear(run);
            }
        }

        return WishAsync(db, relayId, Write, Write, cancellationToken);
    }

    /// <summary>
    /// The board shows the relay come on and nobody in the app asked for it: "outside the app" since
    /// <paramref name="since"/>, with the relay's own <paramref name="autoOffAt"/>. Replaces a run left from before;
    /// one saved by someone else after it was read stays theirs.
    /// </summary>
    public static Task StartOutsideAsync(ContactorDbContext db, int relayId, DateTime since, DateTime? autoOffAt, CancellationToken cancellationToken)
    {
        return WishAsync(db, relayId, run => GiveOwn(run, since, autoOffAt), onConflict: null, cancellationToken);
    }

    /// <summary>
    /// The board shows the relay go off and nobody in the app asked for it: whoever had it on no longer has. A run
    /// saved by someone else after it was read stays theirs.
    /// </summary>
    public static Task EndOutsideAsync(ContactorDbContext db, int relayId, CancellationToken cancellationToken)
    {
        return WishAsync(db, relayId, Clear, onConflict: null, cancellationToken);
    }

    /// <summary>
    /// A relay on with nobody's run gets its own auto-off: "outside the app" since <paramref name="since"/>, with the
    /// relay's own <paramref name="autoOffAt"/>. Only while it still has no run.
    /// </summary>
    public static Task GiveOwnAutoOffAsync(ContactorDbContext db, int relayId, DateTime since, DateTime? autoOffAt, CancellationToken cancellationToken)
    {
        void Write(RelayRuntime run)
        {
            if (run.OnSinceUtc is null)
            {
                GiveOwn(run, since, autoOffAt);
            }
        }

        return WishAsync(db, relayId, Write, Write, cancellationToken);
    }

    /// <summary>
    /// As <see cref="GiveOwnAutoOffAsync"/>, saved at once in one statement that only applies while the relay still
    /// has no run. Returns whether it did.
    /// </summary>
    public static async Task<bool> GiveOwnAutoOffNowAsync(ContactorDbContext db, int relayId, DateTime since, DateTime? autoOffAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        // A bulk update doesn't pass the version interceptor: it raises the version itself.
        var updated = await db.RelayRuntimes
            .Where(r => r.RelayId == relayId && r.OnSinceUtc == null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(r => r.OnSinceUtc, since)
                    .SetProperty(r => r.TurnedOnBy, EventActors.External)
                    .SetProperty(r => r.AutoOffAtUtc, autoOffAt)
                    .SetProperty(r => r.Version, r => r.Version + 1),
                cancellationToken);
        return updated > 0;
    }

    /// <summary>
    /// During a running window of its schedule, a relay on outside the app becomes the schedule's: no auto-off of its
    /// own, off at the OffTime. Only while it is still "outside the app".
    /// </summary>
    public static Task TakeOverForScheduleAsync(ContactorDbContext db, int relayId, CancellationToken cancellationToken)
    {
        static void Write(RelayRuntime run)
        {
            if (run.TurnedOnBy == EventActors.External)
            {
                run.TurnedOnBy = EventActors.Schedule;
                run.AutoOffAtUtc = null;
            }
        }

        return WishAsync(db, relayId, Write, Write, cancellationToken);
    }

    /// <summary>
    /// Moves the auto-off of the run that began at <paramref name="forRunSince"/> to <paramref name="autoOffAt"/>, or
    /// none. Only while that run is still on.
    /// </summary>
    public static Task SetAutoOffAsync(ContactorDbContext db, int relayId, DateTime forRunSince, DateTime? autoOffAt, CancellationToken cancellationToken)
    {
        void Write(RelayRuntime run)
        {
            if (run.OnSinceUtc == forRunSince)
            {
                run.AutoOffAtUtc = autoOffAt;
            }
        }

        return WishAsync(db, relayId, Write, Write, cancellationToken);
    }

    /// <summary>A relay that joins a group has no run of its own any more, whatever was there.</summary>
    public static Task ClearAsync(ContactorDbContext db, int relayId, CancellationToken cancellationToken)
    {
        return WishAsync(db, relayId, Clear, Clear, cancellationToken);
    }

    /// <summary>
    /// Saves everything on <paramref name="db"/>. When another writer changed one of the runs since it was read, that
    /// run is read again, the wishes for it are applied again, and the save is tried again, up to
    /// <see cref="MaxTries"/> times; then it throws <see cref="DbUpdateConcurrencyException"/>.
    /// </summary>
    public static async Task<int> SaveAsync(ContactorDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var saved = await db.SaveChangesAsync(cancellationToken);
                Wishes.Remove(db);
                return saved;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < MaxTries && ex.Entries.All(e => e.Entity is RelayRuntime))
            {
                var wishes = Wishes.TryGetValue(db, out var list) ? list : [];
                foreach (var entry in ex.Entries)
                {
                    var run = (RelayRuntime)entry.Entity;
                    var fresh = await entry.GetDatabaseValuesAsync(cancellationToken)
                        ?? throw new InvalidOperationException($"Relay {run.RelayId}'s run is gone.");
                    entry.OriginalValues.SetValues(fresh);
                    entry.CurrentValues.SetValues(fresh);
                    entry.State = EntityState.Unchanged;
                    foreach (var wish in wishes.Where(w => w.RelayId == run.RelayId))
                    {
                        wish.OnConflict?.Invoke(run);
                    }
                }
            }
        }
    }

    private static async Task WishAsync(ContactorDbContext db, int relayId, Apply apply, Apply? onConflict, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var run = await db.RelayRuntimes.SingleOrDefaultAsync(r => r.RelayId == relayId, cancellationToken);
        if (run is null)
        {
            run = new RelayRuntime { RelayId = relayId };
            db.RelayRuntimes.Add(run);
        }

        apply(run);

        // Saved even when the wish changed nothing in the row as read: the save then still checks the version, and a
        // change someone else saved meanwhile gets the wish applied to it by its rule.
        var entry = db.Entry(run);
        if (entry.State == EntityState.Unchanged)
        {
            entry.State = EntityState.Modified;
        }

        Wishes.GetOrCreateValue(db).Add(new Wish(relayId, onConflict));
    }

    private static void GiveOwn(RelayRuntime run, DateTime since, DateTime? autoOffAt)
    {
        run.OnSinceUtc = since;
        run.TurnedOnBy = EventActors.External;
        run.AutoOffAtUtc = autoOffAt;
    }

    private static void Clear(RelayRuntime run)
    {
        run.OnSinceUtc = null;
        run.AutoOffAtUtc = null;
        run.TurnedOnBy = null;
    }

    // What a writer wants for one relay, to apply again after a conflict; none when someone else's change wins.
    private sealed record Wish(int RelayId, Apply? OnConflict);
}
