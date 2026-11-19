using System.Text.Json;
using System.Threading.Channels;
using Contactor.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.BoardRules;

/// <summary>A board's own rule as the app remembers it (<see cref="BoardOwnRule"/>): the relay's channel, its kind, and whether a person kept it.</summary>
public sealed record OwnRuleRow(int Channel, OwnRuleKind Kind, DateTime FoundUtc, DateTime? KeptUtc, string? KeptBy);

/// <summary>
/// What became of a person's "Keep" (<see cref="OwnRulesService.KeepAsync"/>): kept (also when it was already), refused
/// because the relay on <see cref="InGroup"/> is in a group and the rule may not be kept there, or gone.
/// </summary>
internal readonly record struct KeepOutcome(bool Kept, int? InGroup)
{
    public static KeepOutcome Done { get; } = new(Kept: true, InGroup: null);

    public static KeepOutcome Gone { get; } = new(Kept: false, InGroup: null);

    public static KeepOutcome Grouped(int channel)
    {
        return new(Kept: false, InGroup: channel);
    }
}

/// <summary>
/// Remembers the rules of their own each board reports (<see cref="BoardState.OwnRules"/>, issue #497) and logs what it
/// finds, so the screens can warn about them. It never sends a command to a board: the app changes a board's own rules
/// only on a person's press (<see cref="OwnRulesHandler"/>).
/// <para>
/// It listens to every board through <see cref="BoardRegistry"/> (<see cref="BoardRegistry.StateChanged"/> and
/// <see cref="BoardRegistry.OwnRulesChanged"/>), keeps only each board's latest state, and does the database work on
/// its own loop, never on a board's read loop, and acts only when a board's rules differ from the last it saw:
/// </para>
/// <list type="bullet">
/// <item>a rule with no row gets one, and is logged as <c>board-own-rule-found</c> by "system", with <c>new</c> true
/// when this run had already read the board's rules without it (it was turned on while the app watched);</item>
/// <item>a row whose rule an actual reading no longer has is deleted, and its "Keep" with it, without logging;</item>
/// <item>no reading (null rules: the board can't read them, hasn't answered, or its state was forgotten) changes no row.</item>
/// </list>
/// <para>
/// A board whose state is forgotten after its rules were read (a new address or another model: another board may be
/// there) has its rows deleted, as the address and the model do when they change (<see cref="BoardAddressHandler"/>,
/// <see cref="BoardRegistry.ReplaceModelAsync"/>). It keeps each board's rows in memory for the screens, and raises
/// <see cref="Changed"/> when they change. It is no board watcher: the safety services never wait for it.
/// </para>
/// </summary>
public sealed partial class OwnRulesService : BackgroundService
{
    /// <summary>How long a board whose rules couldn't be saved waits before they are tried again.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly BoardRegistry _registry;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly LocalClock _clock;
    private readonly TimeProvider _time;
    private readonly ILogger<OwnRulesService> _logger;

    // The boards with work waiting, and each one's latest state: one entry per board, so a board that reports often
    // never queues up more than one.
    private readonly Lock _lock = new();
    private readonly Dictionary<int, Pending> _pending = [];
    private readonly Channel<int> _due = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });

    // The database work, the loop's and a person's "Keep", one at a time.
    private readonly SemaphoreSlim _work = new(1, 1);

    // Only under _work: the rules last read from each board since its state was last forgotten, null when none was read.
    private readonly Dictionary<int, OwnRules?> _seen = [];

    // Each board's rows, for the screens; replaced as a whole, under _lock.
    private readonly Dictionary<int, IReadOnlyList<OwnRuleRow>> _rows = [];

    // The boards waiting for a retry, and whether their rows must go then, under _lock.
    private readonly Dictionary<int, bool> _retrying = [];

    private CancellationToken _stopping;
    private int _subscribed;

    public OwnRulesService(
        BoardRegistry registry,
        IDbContextFactory<ContactorDbContext> dbFactory,
        LocalClock clock,
        TimeProvider time,
        ILogger<OwnRulesService> logger)
    {
        _registry = registry;
        _dbFactory = dbFactory;
        _clock = clock;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// A board's rows changed (<see cref="Rows"/>), with its id. Raised on the service's own loop, or after a "Keep"
    /// on the thread that kept it, never while the service's database work is held; a screen moves to its own thread
    /// to draw.
    /// </summary>
    public event EventHandler<int>? Changed;

    /// <summary>The board's own rules the app remembers, by channel and kind; none for a board it hasn't read yet.</summary>
    public IReadOnlyList<OwnRuleRow> Rows(int boardId)
    {
        return TryGetRows(boardId, out var rows) ? rows : [];
    }

    /// <summary>
    /// The board's own rules the app remembers, once it has read the board's rows; false before (just started, or a
    /// board it hasn't seen yet), when what a person kept isn't known.
    /// </summary>
    public bool TryGetRows(int boardId, out IReadOnlyList<OwnRuleRow> rows)
    {
        lock (_lock)
        {
            if (_rows.TryGetValue(boardId, out var known))
            {
                rows = known;
                return true;
            }
        }

        rows = [];
        return false;
    }

    /// <summary>
    /// The board is another board now (a new address): its rows are deleted and it is read afresh from its next state,
    /// as one never seen. Its rows may already be gone; nothing is sent to the board.
    /// </summary>
    public void Forget(int boardId)
    {
        Enqueue(boardId, null, forget: true);
    }

    // Subscribes here rather than in ExecuteAsync, which .NET runs on a background thread: nothing a board reports
    // after the start is missed. Every board there is now is read at once.
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _subscribed, 1) == 0)
        {
            _registry.StateChanged += OnStateChanged;
            _registry.OwnRulesChanged += OnStateChanged;
            _registry.BoardsChanged += OnBoardsChanged;
        }

        OnBoardsChanged(this, EventArgs.Empty);
        return base.StartAsync(cancellationToken);
    }

    public override void Dispose()
    {
        Unsubscribe();
        _work.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        try
        {
            while (await _due.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_due.Reader.TryRead(out var boardId))
                {
                    Pending pending;
                    lock (_lock)
                    {
                        if (!_pending.Remove(boardId, out pending))
                        {
                            continue;
                        }
                    }

                    await _work.WaitAsync(stoppingToken);
                    try
                    {
                        await HandleAsync(boardId, pending, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
#pragma warning disable CA1031 // A failure here must not stop the other boards' rules; it is logged and tried again.
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        // Read afresh when tried again; rows a forgotten board's old rules left go then.
                        var wasRead = _seen.GetValueOrDefault(boardId) is not null;
                        _seen.Remove(boardId);
                        LogFailed(ex, boardId);
                        RetryLater(boardId, pending.Forget || (pending.Reset && wasRead));
                    }
                    finally
                    {
                        _work.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            Unsubscribe();
        }
    }

    /// <summary>
    /// Marks the board's rule as kept by <paramref name="userEmail"/> and logs it: the app no longer warns about it.
    /// The keep is one conditional write (<see cref="MarkKeptAsync"/>), so a group saved at the same moment is never
    /// crossed: for the auto-on of a relay that is in a group by then, nothing is saved and the outcome names its
    /// channel. Gone when the app has no such rule (any more), also when its row goes meanwhile (a new address or
    /// another model deletes it), and for a kind no person may keep (<see cref="OwnRulesHandler.CanKeep"/>); kept,
    /// logging nothing again, when it was kept already.
    /// </summary>
    internal async Task<KeepOutcome> KeepAsync(int boardId, int channel, OwnRuleKind kind, string userEmail, CancellationToken cancellationToken)
    {
        if (!OwnRulesHandler.CanKeep(kind, inGroup: false))
        {
            return KeepOutcome.Gone;
        }

        var changed = false;
        await _work.WaitAsync(cancellationToken);
        try
        {
            var text = OwnRuleKinds.For(kind);
            List<BoardOwnRule> rows;
            await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
            {
                var row = await db.BoardOwnRules.AsNoTracking().SingleOrDefaultAsync(r => r.BoardId == boardId && r.Channel == channel && r.Kind == text, cancellationToken);
                if (row is null)
                {
                    return KeepOutcome.Gone;
                }

                if (row.KeptUtc is not null)
                {
                    return KeepOutcome.Done;
                }

                // Read before the write: the transaction holds the database only for the write and its event.
                var relayId = await db.Relays.Where(r => r.BoardId == boardId && r.Channel == channel).Select(r => (int?)r.Id).SingleOrDefaultAsync(cancellationToken);
                var now = _clock.UtcNow;
                await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
                {
                    if (await MarkKeptAsync(db, boardId, channel, text, now, userEmail, cancellationToken) == 0)
                    {
                        // Nothing written: the relay joined a group, the row went, or someone else kept it meanwhile.
                        return await NotMarkedAsync(db, boardId, channel, text, cancellationToken);
                    }

                    db.EventLog.Add(new EventLogEntry
                    {
                        TimestampUtc = now,
                        BoardId = boardId,
                        RelayId = relayId,
                        Actor = userEmail,
                        Action = EventActions.BoardOwnRuleKept,
                        Details = JsonSerializer.Serialize(new { kind = text, relay = channel }),
                    });
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }

                rows = await db.BoardOwnRules.AsNoTracking().Where(r => r.BoardId == boardId).ToListAsync(cancellationToken);
            }

            changed = Publish(boardId, rows);
            return KeepOutcome.Done;
        }
        finally
        {
            _work.Release();
            if (changed)
            {
                RaiseChanged(boardId);
            }
        }
    }

    /// <summary>
    /// "Keep them" while the board is being added: marks each of <paramref name="rules"/> as kept by
    /// <paramref name="userEmail"/>, and logs each once, also a rule the service hasn't noted yet: its row is made here,
    /// logged as found as the service would, so a person never waits for the service's save (which, after a failure,
    /// is tried again only 30 s later). Runs under the same lock as the service's own work, so the service never
    /// inserts the same row at the same time, and once this row is there it finds it and logs nothing more. Each keep
    /// is one conditional write (<see cref="MarkKeptAsync"/>), all in one transaction. Only rules the board reports now
    /// are kept; gone, saving nothing, when it no longer reports one of them, and refused, saving nothing, with the
    /// channel, when one is a rule no person may keep on its relay (<see cref="OwnRulesHandler.CanKeep"/>), also when
    /// the relay joined a group meanwhile.
    /// </summary>
    internal async Task<KeepOutcome> KeepReportedAsync(int boardId, IReadOnlyCollection<OwnRule> rules, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var changed = false;
        await _work.WaitAsync(cancellationToken);
        try
        {
            if (_registry.Find(boardId)?.State.OwnRules is not { } reported || !rules.All(reported.Contains) || !rules.All(r => OwnRulesHandler.CanKeep(r.Kind, inGroup: false)))
            {
                return KeepOutcome.Gone;
            }

            List<BoardOwnRule> rows;
            await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
            {
                var grouped = await GroupedRelaysAsync(db, boardId, cancellationToken);
                if (rules.FirstOrDefault(r => !OwnRulesHandler.CanKeep(r.Kind, grouped.ContainsKey(r.Relay))) is { } refused)
                {
                    return KeepOutcome.Grouped(refused.Relay);
                }

                var now = _clock.UtcNow;
                var existing = await db.BoardOwnRules.Where(r => r.BoardId == boardId).ToListAsync(cancellationToken);
                List<(int Channel, string Kind, int? RelayId)> kept = [];
                var relays = await db.Relays.Where(r => r.BoardId == boardId).ToDictionaryAsync(r => r.Channel, r => r.Id, cancellationToken);
                foreach (var rule in rules.Distinct())
                {
                    var kind = OwnRuleKinds.For(rule.Kind);
                    int? relayId = relays.TryGetValue(rule.Relay, out var id) ? id : null;
                    var row = existing.SingleOrDefault(r => r.Channel == rule.Relay && r.Kind == kind);
                    if (row is null)
                    {
                        row = new BoardOwnRule { BoardId = boardId, Channel = rule.Relay, Kind = kind, FoundUtc = now };
                        db.BoardOwnRules.Add(row);
                        db.EventLog.Add(new EventLogEntry
                        {
                            TimestampUtc = now,
                            BoardId = boardId,
                            RelayId = relayId,
                            Actor = EventActors.System,
                            Action = EventActions.BoardOwnRuleFound,
                            Details = JsonSerializer.Serialize(new { kind, @new = _seen.GetValueOrDefault(boardId) is { } seen && !seen.Contains(rule), relay = rule.Relay }),
                        });
                    }
                    else if (row.KeptUtc is not null)
                    {
                        continue;
                    }

                    kept.Add((rule.Relay, kind, relayId));
                }

                // Read before the writes: the transaction holds the database only for them and their events.
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                try
                {
                    // The new rows first, not kept: each keep is then the same conditional write as for one.
                    await db.SaveChangesAsync(cancellationToken);
                    foreach (var (channel, kind, _) in kept)
                    {
                        if (await MarkKeptAsync(db, boardId, channel, kind, now, userEmail, cancellationToken) == 0)
                        {
                            // Rolled back with the transaction: nothing is saved or logged.
                            return await NotMarkedAsync(db, boardId, channel, kind, cancellationToken);
                        }
                    }
                }
                catch (DbUpdateException)
                {
                    // A row deleted meanwhile (a new address, another model, which don't take this lock), or one made by
                    // something else despite the lock: nothing was saved or logged, and the person is told so.
                    return KeepOutcome.Gone;
                }

                foreach (var (channel, kind, relayId) in kept)
                {
                    db.EventLog.Add(new EventLogEntry
                    {
                        TimestampUtc = now,
                        BoardId = boardId,
                        RelayId = relayId,
                        Actor = userEmail,
                        Action = EventActions.BoardOwnRuleKept,
                        Details = JsonSerializer.Serialize(new { kind, relay = channel }),
                    });
                }

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                rows = await db.BoardOwnRules.AsNoTracking().Where(r => r.BoardId == boardId).ToListAsync(cancellationToken);
            }

            changed = Publish(boardId, rows);
            return KeepOutcome.Done;
        }
        finally
        {
            _work.Release();
            if (changed)
            {
                RaiseChanged(boardId);
            }
        }
    }

    // Keeps the board's rule in one statement: only a row not kept yet, and for a kind a person may keep only on a relay
    // in no group (OwnRulesHandler.KeptOnlyOutsideGroups) only while its relay is in no group. SQLite checks that in the
    // same UPDATE, so a group saved at the same moment, which doesn't take this service's lock, is never crossed: either
    // the group's save comes first and nothing is written here, or this comes first and the group's save forgets it
    // (ForgetKeepsInGroupsAsync). The rows written: 1, or 0.
    private static Task<int> MarkKeptAsync(ContactorDbContext db, int boardId, int channel, string kind, DateTime now, string userEmail, CancellationToken cancellationToken)
    {
        var outsideOnly = OutsideGroupKinds();
        return db.BoardOwnRules
            .Where(r => r.BoardId == boardId && r.Channel == channel && r.Kind == kind && r.KeptUtc == null)
            .Where(r => !outsideOnly.Contains(r.Kind) || !db.RelayGroupMembers.Any(m => m.Relay!.BoardId == boardId && m.Relay.Channel == channel))
            .ExecuteUpdateAsync(r => r.SetProperty(x => x.KeptUtc, now).SetProperty(x => x.KeptBy, userEmail), cancellationToken);
    }

    // Why a keep wrote nothing: refused when the rule may not be kept on its relay, which is in a group now; done when
    // someone kept it meanwhile; otherwise gone.
    private static async Task<KeepOutcome> NotMarkedAsync(ContactorDbContext db, int boardId, int channel, string kind, CancellationToken cancellationToken)
    {
        if (OutsideGroupKinds().Contains(kind) && await db.RelayGroupMembers.AnyAsync(m => m.Relay!.BoardId == boardId && m.Relay.Channel == channel, cancellationToken))
        {
            return KeepOutcome.Grouped(channel);
        }

        return await db.BoardOwnRules.AnyAsync(r => r.BoardId == boardId && r.Channel == channel && r.Kind == kind && r.KeptUtc != null, cancellationToken)
            ? KeepOutcome.Done
            : KeepOutcome.Gone;
    }

    // The kinds, as saved, a person may keep only on a relay in no group.
    private static List<string> OutsideGroupKinds()
    {
        return [.. OwnRulesHandler.KeptOnlyOutsideGroups.Select(OwnRuleKinds.For)];
    }

    /// <summary>The board's relays that are in a group, by channel, with their names.</summary>
    public static async Task<Dictionary<int, string>> GroupedRelaysAsync(ContactorDbContext db, int boardId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.RelayGroupMembers
            .Where(m => m.Relay!.BoardId == boardId)
            .Select(m => new { m.Relay!.Channel, m.Relay.Name })
            .ToDictionaryAsync(m => m.Channel, m => m.Name, cancellationToken);
    }

    /// <summary>
    /// Forgets the "Keep" of every rule of the board's own that may not be kept on a relay in a group
    /// (<see cref="OwnRulesHandler.CanKeep"/>, e.g. the auto-on) on the board's relays that are in a group now, so the
    /// app warns about it again; also if the relay leaves the group later. Run in the transaction that saves a group's
    /// relays, after they are saved; <see cref="ReloadAsync"/> shows it once committed. Logs nothing.
    /// </summary>
    public static async Task ForgetKeepsInGroupsAsync(ContactorDbContext db, int boardId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var kinds = OutsideGroupKinds();
        var grouped = db.RelayGroupMembers.Where(m => m.Relay!.BoardId == boardId).Select(m => m.Relay!.Channel);
        await db.BoardOwnRules
            .Where(r => r.BoardId == boardId && r.KeptUtc != null && kinds.Contains(r.Kind) && grouped.Contains(r.Channel))
            .ExecuteUpdateAsync(r => r.SetProperty(x => x.KeptUtc, (DateTime?)null).SetProperty(x => x.KeptBy, (string?)null), cancellationToken);
    }

    /// <summary>
    /// The board's groups changed: its rows are read again (<see cref="ForgetKeepsInGroupsAsync"/> may have changed them)
    /// and <see cref="Changed"/> is raised, so the screens see which relays are in a group now. A board whose rows the
    /// service hasn't read yet is left to its first reading. A failed read is logged, not thrown: the group is saved.
    /// </summary>
    public async Task ReloadAsync(int boardId, CancellationToken cancellationToken)
    {
        await _work.WaitAsync(cancellationToken);
        try
        {
            if (TryGetRows(boardId, out _))
            {
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
                Publish(boardId, await db.BoardOwnRules.AsNoTracking().Where(r => r.BoardId == boardId).ToListAsync(cancellationToken));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The group is saved: the screens keep what they had until the board's rows are read again.
            LogReloadFailed(ex, boardId);
        }
        finally
        {
            _work.Release();
        }

        RaiseChanged(boardId);
    }

    private void Unsubscribe()
    {
        if (Interlocked.Exchange(ref _subscribed, 0) == 1)
        {
            _registry.StateChanged -= OnStateChanged;
            _registry.OwnRulesChanged -= OnStateChanged;
            _registry.BoardsChanged -= OnBoardsChanged;
        }
    }

    // On a board's read loop: only notes the state.
    private void OnStateChanged(object? sender, BoardStateChange change)
    {
        Enqueue(change.BoardId, change.State, forget: false);
    }

    // Every board there is now is read, also one just made, and one removed is let go.
    private void OnBoardsChanged(object? sender, EventArgs e)
    {
        var boards = _registry.All;
        List<int> gone;
        lock (_lock)
        {
            gone = [.. _rows.Keys.Where(id => !boards.Any(b => b.BoardId == id))];
        }

        foreach (var board in boards)
        {
            Enqueue(board.BoardId, board.State, forget: false);
        }

        foreach (var boardId in gone)
        {
            Enqueue(boardId, null, forget: false);
        }
    }

    private void Enqueue(int boardId, BoardState? state, bool forget)
    {
        lock (_lock)
        {
            var queued = _pending.TryGetValue(boardId, out var pending);

            // A forgotten state is kept in mind until handled, also when newer states come first: the board there may be another.
            _pending[boardId] = new Pending(
                state,
                IsForgotten(state) || (queued && pending.Reset),
                forget || (queued && pending.Forget));
            if (!queued)
            {
                _due.Writer.TryWrite(boardId);
            }
        }
    }

    private void RetryLater(int boardId, bool forget)
    {
        lock (_lock)
        {
            if (_retrying.TryGetValue(boardId, out var waiting))
            {
                _retrying[boardId] = waiting || forget;
                return;
            }

            _retrying[boardId] = forget;
        }

        _ = RetryAsync(boardId);
    }

    private async Task RetryAsync(int boardId)
    {
        try
        {
            await Task.Delay(RetryDelay, _time, _stopping);
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                _retrying.Remove(boardId);
            }

            return;
        }

        bool forget;
        lock (_lock)
        {
            _retrying.Remove(boardId, out forget);
        }

        Enqueue(boardId, _registry.Find(boardId)?.State, forget);
    }

    private async Task HandleAsync(int boardId, Pending pending, CancellationToken cancellationToken)
    {
        if (_registry.Find(boardId) is null)
        {
            // Removed: its rows went with it.
            _seen.Remove(boardId);
            bool had;
            lock (_lock)
            {
                had = _rows.Remove(boardId);
            }

            if (had)
            {
                RaiseChanged(boardId);
            }

            return;
        }

        // The board's rows read afresh: the first time, and whenever its state was forgotten (at start, a new address,
        // another model), whose rows the address or the model may have deleted meanwhile.
        var loaded = _seen.TryGetValue(boardId, out var seen);
        if (pending.Forget || pending.Reset || !loaded)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            // Forgotten after its rules were read: a new address or another model, and another board may be there.
            if (pending.Forget || (pending.Reset && seen is not null))
            {
                await db.BoardOwnRules.Where(r => r.BoardId == boardId).ExecuteDeleteAsync(cancellationToken);
                LogForgotten(boardId);
            }

            PublishAndRaise(boardId, await db.BoardOwnRules.AsNoTracking().Where(r => r.BoardId == boardId).ToListAsync(cancellationToken));
            seen = null;
            _seen[boardId] = null;
        }

        var rules = pending.State?.OwnRules;
        if (rules is null || rules == seen)
        {
            return;
        }

        await ActAsync(boardId, rules, seen, cancellationToken);
        _seen[boardId] = rules;
    }

    // The board read rules other than the last: a row for each new rule, logged, and none for a rule it no longer has.
    private async Task ActAsync(int boardId, OwnRules rules, OwnRules? seen, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.BoardOwnRules.Where(r => r.BoardId == boardId).ToListAsync(cancellationToken);
        var reported = rules.Distinct().ToList();
        var now = _clock.UtcNow;
        Dictionary<int, int>? relays = null;
        foreach (var rule in reported)
        {
            var kind = OwnRuleKinds.For(rule.Kind);
            if (rows.Any(r => r.Channel == rule.Relay && r.Kind == kind))
            {
                continue;
            }

            relays ??= await db.Relays.Where(r => r.BoardId == boardId).ToDictionaryAsync(r => r.Channel, r => r.Id, cancellationToken);
            db.BoardOwnRules.Add(new BoardOwnRule { BoardId = boardId, Channel = rule.Relay, Kind = kind, FoundUtc = now });
            db.EventLog.Add(new EventLogEntry
            {
                TimestampUtc = now,
                BoardId = boardId,
                RelayId = relays.TryGetValue(rule.Relay, out var relayId) ? relayId : null,
                Actor = EventActors.System,
                Action = EventActions.BoardOwnRuleFound,
                Details = JsonSerializer.Serialize(new { kind, @new = seen is not null && !seen.Contains(rule), relay = rule.Relay }),
            });
        }

        // A rule the reading no longer has: its row, and its "Keep" with it, so it warns again if it comes back.
        foreach (var row in rows)
        {
            if (!OwnRuleKinds.TryParse(row.Kind, out var kind) || !reported.Contains(new OwnRule(row.Channel, kind)))
            {
                db.BoardOwnRules.Remove(row);
            }
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(cancellationToken);
            PublishAndRaise(boardId, await db.BoardOwnRules.AsNoTracking().Where(r => r.BoardId == boardId).ToListAsync(cancellationToken));
        }
    }

    // The board's rows for the screens; true when they differ from what they were, so Changed is raised.
    private bool Publish(int boardId, List<BoardOwnRule> rows)
    {
        IReadOnlyList<OwnRuleRow> view =
        [
            .. rows
                .Select(r => OwnRuleKinds.TryParse(r.Kind, out var kind) ? new OwnRuleRow(r.Channel, kind, r.FoundUtc, r.KeptUtc, r.KeptBy) : null)
                .OfType<OwnRuleRow>()
                .OrderBy(r => r.Channel)
                .ThenBy(r => r.Kind),
        ];
        bool changed;
        lock (_lock)
        {
            changed = !_rows.TryGetValue(boardId, out var before) || !before.SequenceEqual(view);
            _rows[boardId] = view;
        }

        return changed;
    }

    // On the loop: the board's rows for the screens, and Changed when they differ from what they were.
    private void PublishAndRaise(int boardId, List<BoardOwnRule> rows)
    {
        if (Publish(boardId, rows))
        {
            RaiseChanged(boardId);
        }
    }

    private void RaiseChanged(int boardId)
    {
        if (Changed is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<int>>())
        {
            try
            {
                handler(this, boardId);
            }
#pragma warning disable CA1031 // A listener's failure is logged; the others are still told.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogListenerFailed(ex, boardId);
            }
        }
    }

    // Nothing known about the board: its address changed (or the app just started), and another board may be there.
    private static bool IsForgotten(BoardState? state)
    {
        return state is { IsConnected: false, LastContactUtc: null };
    }

    // A board's latest state, whether it was forgotten since the last handled, and whether its rows must go.
    private readonly record struct Pending(BoardState? State, bool Reset, bool Forget);

    [LoggerMessage(Level = LogLevel.Information, Message = "Forgot the own rules of relay board {BoardId}: another board may be at its address")]
    private partial void LogForgotten(int boardId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not save the own rules of relay board {BoardId}; tried again later")]
    private partial void LogFailed(Exception exception, int boardId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the own rules of relay board {BoardId} again after its groups changed")]
    private partial void LogReloadFailed(Exception exception, int boardId);

    [LoggerMessage(Level = LogLevel.Error, Message = "A listener to the own rules of relay board {BoardId} failed")]
    private partial void LogListenerFailed(Exception exception, int boardId);
}
