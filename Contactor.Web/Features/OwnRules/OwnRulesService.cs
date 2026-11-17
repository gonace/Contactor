using System.Text.Json;
using System.Threading.Channels;
using Contactor.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using ReportedRules = Contactor.Boards.OwnRules;

namespace Contactor.Web.Features.OwnRules;

/// <summary>A board's own rule as the app remembers it (<see cref="BoardOwnRule"/>): the relay's channel, its kind, and whether a person kept it.</summary>
public sealed record OwnRuleRow(int Channel, OwnRuleKind Kind, DateTime FoundUtc, DateTime? KeptUtc, string? KeptBy);

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
    private readonly Dictionary<int, ReportedRules?> _seen = [];

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
    /// A board's rows changed (<see cref="Rows"/>), with its id. Raised on the service's own loop; a screen moves to its
    /// own thread to draw.
    /// </summary>
    public event EventHandler<int>? Changed;

    /// <summary>The board's own rules the app remembers, by channel and kind; none for a board it hasn't read yet.</summary>
    public IReadOnlyList<OwnRuleRow> Rows(int boardId)
    {
        lock (_lock)
        {
            return _rows.GetValueOrDefault(boardId) ?? [];
        }
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
    /// Returns false when the app has no such rule (any more), and true, logging nothing again, when it was kept already.
    /// </summary>
    internal async Task<bool> KeepAsync(int boardId, int channel, OwnRuleKind kind, string userEmail, CancellationToken cancellationToken)
    {
        await _work.WaitAsync(cancellationToken);
        try
        {
            var text = OwnRuleKinds.For(kind);
            List<BoardOwnRule> rows;
            await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
            {
                var row = await db.BoardOwnRules.SingleOrDefaultAsync(r => r.BoardId == boardId && r.Channel == channel && r.Kind == text, cancellationToken);
                if (row is null)
                {
                    return false;
                }

                if (row.KeptUtc is not null)
                {
                    return true;
                }

                row.KeptUtc = _clock.UtcNow;
                row.KeptBy = userEmail;
                db.EventLog.Add(new EventLogEntry
                {
                    TimestampUtc = row.KeptUtc.Value,
                    BoardId = boardId,
                    RelayId = await db.Relays.Where(r => r.BoardId == boardId && r.Channel == channel).Select(r => (int?)r.Id).SingleOrDefaultAsync(cancellationToken),
                    Actor = userEmail,
                    Action = EventActions.BoardOwnRuleKept,
                    Details = JsonSerializer.Serialize(new { kind = text, relay = channel }),
                });
                await db.SaveChangesAsync(cancellationToken);
                rows = await db.BoardOwnRules.AsNoTracking().Where(r => r.BoardId == boardId).ToListAsync(cancellationToken);
            }

            Publish(boardId, rows);
            return true;
        }
        finally
        {
            _work.Release();
        }
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

            Publish(boardId, await db.BoardOwnRules.AsNoTracking().Where(r => r.BoardId == boardId).ToListAsync(cancellationToken));
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
    private async Task ActAsync(int boardId, ReportedRules rules, ReportedRules? seen, CancellationToken cancellationToken)
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
            Publish(boardId, await db.BoardOwnRules.AsNoTracking().Where(r => r.BoardId == boardId).ToListAsync(cancellationToken));
        }
    }

    // The board's rows for the screens, and Changed when they differ from what they were.
    private void Publish(int boardId, List<BoardOwnRule> rows)
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

        if (changed)
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

    [LoggerMessage(Level = LogLevel.Error, Message = "A listener to the own rules of relay board {BoardId} failed")]
    private partial void LogListenerFailed(Exception exception, int boardId);
}
