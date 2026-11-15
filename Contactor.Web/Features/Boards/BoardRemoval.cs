using System.Globalization;
using Contactor.Boards;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Boards;

/// <summary>What removing a board deletes, for the question the board's page asks first.</summary>
/// <param name="BoardId">The board's row.</param>
/// <param name="Name">What people call it.</param>
/// <param name="Relays">Its relays.</param>
/// <param name="Groups">Its groups.</param>
/// <param name="Schedules">The schedules of its relays and groups.</param>
/// <param name="InputActions">The input actions that touch its relays, groups or inputs.</param>
/// <param name="HasContact">Whether the board answers now, so its relays can be turned off first.</param>
public sealed record BoardRemovalPreview(int BoardId, string Name, int Relays, int Groups, int Schedules, int InputActions, bool HasContact);

/// <summary>
/// Takes a board out of the app, on its page in Settings, one removal at a time. From the person's confirm the board is
/// closed to every new ON (<see cref="LiveBoard.CloseAsync"/>): no tap, schedule, input action or group step can turn a relay
/// on. A board that answers then has its relays turned off: "All off" for that board
/// (<see cref="RelayControl.TurnAllOffAsync"/>), its groups stopping in their order with their run-on times. Once the
/// board shows every relay off, within <see cref="OffWait"/>, its live board is stopped and let go
/// (<see cref="BoardRegistry.RemoveAsync"/>) and everything about it is deleted in one transaction, its events included.
/// A group still running on stops it: nothing is removed, the board is opened again, and the person is told when the
/// run-on ends. A board without contact is removed as it is: whatever is on stays on. The board itself isn't changed.
/// The last board can be removed too: the app then has none until one is added (<see cref="BoardRegistry.AddBoardAsync(BoardModel, string?, CancellationToken)"/>).
/// </summary>
public sealed partial class BoardRemoval : IDisposable
{
    /// <summary>How long the board has to show every relay off before removing it is given up.</summary>
    public static readonly TimeSpan OffWait = TimeSpan.FromSeconds(10);

    /// <summary>How often the board's relays are looked at meanwhile.</summary>
    public static readonly TimeSpan OffCheck = TimeSpan.FromMilliseconds(500);

    /// <summary>What removing says when someone else removed the board first.</summary>
    public const string AlreadyRemoved = "This relay board was removed already.";

    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly BoardRegistry _registry;
    private readonly RelayControl _control;
    private readonly GroupSequencer _groups;
    private readonly RelayChanges _changes;
    private readonly LocalClock _clock;
    private readonly TimeProvider _time;
    private readonly ILogger<BoardRemoval> _logger;

    // One removal at a time: a failed one's restore can't meet another's.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BoardRemoval(
        IDbContextFactory<ContactorDbContext> dbFactory,
        BoardRegistry registry,
        RelayControl control,
        GroupSequencer groups,
        RelayChanges changes,
        LocalClock clock,
        TimeProvider time,
        ILogger<BoardRemoval> logger)
    {
        _dbFactory = dbFactory;
        _registry = registry;
        _control = control;
        _groups = groups;
        _changes = changes;
        _clock = clock;
        _time = time;
        _logger = logger;
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <summary>What removing the board would delete, or null when there is no such board.</summary>
    public async Task<BoardRemovalPreview?> PreviewAsync(int boardId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var name = await db.Boards.Where(b => b.Id == boardId).Select(b => b.Name).SingleOrDefaultAsync(cancellationToken);
        if (name is null)
        {
            return null;
        }

        var of = new Of(db, boardId);
        return new BoardRemovalPreview(
            boardId,
            name,
            await of.Relays.CountAsync(cancellationToken),
            await of.Groups.CountAsync(cancellationToken),
            await of.Schedules.CountAsync(cancellationToken),
            await of.InputActions.CountAsync(cancellationToken),
            _registry.Find(boardId)?.State.IsConnected == true);
    }

    /// <summary>
    /// Removes the board. <paramref name="turnOffFirst"/> is what the person was told: true when its relays would be
    /// turned off first, so a board that has lost contact since isn't removed then. A board that answers always has its
    /// relays turned off first. Returns null when removed, otherwise the message to show; nothing is removed then, and
    /// the board works as before.
    /// </summary>
    public async Task<string?> RemoveAsync(int boardId, bool turnOffFirst, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            string? name;
            await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
            {
                name = await db.Boards.Where(b => b.Id == boardId).Select(b => b.Name).SingleOrDefaultAsync(cancellationToken);
            }

            if (name is null)
            {
                return AlreadyRemoved;
            }

            var live = _registry.Find(boardId);
            var removed = false;
            try
            {
                // Closed to every new ON, and every ON already under way has landed: none comes after the last look.
                if (live is not null)
                {
                    await live.CloseAsync();
                }

                if (live is not null && live.State.IsConnected)
                {
                    var (error, lost) = await TurnOffAsync(live, name, userEmail, cancellationToken);
                    if (error is not null && (turnOffFirst || !lost))
                    {
                        return error;
                    }
                }
                else if (turnOffFirst)
                {
                    return LostText(name);
                }

                // Nothing acts on the board from here on: its watches end, then its connection.
                await _registry.RemoveAsync(boardId, cancellationToken);
                removed = true;
                try
                {
                    await DeleteAsync(_dbFactory, boardId, cancellationToken);
                }
                catch
                {
                    // Nothing was deleted: the board runs again, in its place.
                    try
                    {
                        await _registry.RestoreAsync(boardId, CancellationToken.None);
                    }
#pragma warning disable CA1031 // The failure to delete is the one thrown; this one is logged.
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        LogNotRestored(ex, boardId);
                    }

                    throw;
                }

                await _registry.ForgetRemovedAsync(boardId, CancellationToken.None);
            }
            finally
            {
                if (!removed)
                {
                    live?.Reopen();
                }
            }

            LogRemoved(boardId);
            _changes.Notify();
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string LostText(string name)
    {
        return $"{name} has lost contact, so its relays couldn't be turned off. Nothing was removed.";
    }

    // "All off" for the board, its groups stopping in their order with their run-on times, until the board shows every
    // relay off and no group is stopping. The board is closed meanwhile, so nothing but a stop's run-on turns one on
    // again; a relay that is on again all the same gets "All off" once more, which logs no "Off": only the first does.
    // A group still running on when the wait ends, or whose run-on ends after it, stops the removal. Returns why not,
    // and whether that was a lost contact.
    private async Task<(string? Error, bool Lost)> TurnOffAsync(LiveBoard live, string name, string userEmail, CancellationToken cancellationToken)
    {
        var boardId = live.BoardId;
        try
        {
            var all = live.Capabilities.AllRelays;
            await _control.TurnAllOffAsync(boardId, userEmail, cancellationToken);
            var deadline = _clock.UtcNow + OffWait;
            while (true)
            {
                // Under the board's group lock: no group step is half done (a trip's OFF sent, its run-on's ON not yet).
                var (state, stopping) = await _groups.ExclusiveAsync(
                    boardId,
                    async () => (live.State, await StoppingAsync(live, cancellationToken)),
                    cancellationToken);
                var now = _clock.UtcNow;
                if (!state.IsConnected)
                {
                    return (LostText(name), true);
                }

                var runOn = stopping.OrderByDescending(g => g.Off).FirstOrDefault();
                if (runOn is not null && runOn.Off > now && (runOn.Off > deadline || now >= deadline))
                {
                    return ($"{runOn.Name} runs on until {_clock.ToLocal(runOn.Off).ToString("HH:mm", CultureInfo.InvariantCulture)}. Remove {name} when it's done.", false);
                }

                if (stopping.Count == 0 && (state.Relays & all).IsEmpty)
                {
                    return (null, false);
                }

                if (now >= deadline)
                {
                    return ($"Some of {name}'s relays didn't turn off. Nothing was removed. Try again.", false);
                }

                await Task.Delay(OffCheck, _time, cancellationToken);
                if (!(live.State.Relays & all).IsEmpty)
                {
                    // Once more for what is still on, without logging it again: a group that is already stopping
                    // carries on as it was.
                    await _control.TurnAllOffAgainAsync(boardId, userEmail, cancellationToken);
                }
            }
        }
        catch (BoardNotConnectedException)
        {
            return (LostText(name), true);
        }
    }

    // The board's groups that are stopping, each with when its last relay goes off: the run-on under way (or about to
    // start, a step the board hasn't confirmed yet) and every run-on above it, each counted from the one below.
    private async Task<List<Stopping>> StoppingAsync(LiveBoard live, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var groups = await db.RelayGroups.AsNoTracking()
            .Where(g => g.BoardId == live.BoardId)
            .Include(g => g.Members).ThenInclude(m => m.Relay)
            .ToListAsync(cancellationToken);
        var ids = groups.Select(g => g.Id).ToList();
        var runtimes = await db.GroupRuntimes.AsNoTracking()
            .Where(r => ids.Contains(r.GroupId) && r.Phase == GroupPhases.Stopping)
            .ToDictionaryAsync(r => r.GroupId, cancellationToken);
        var now = _clock.UtcNow;
        var state = live.State;
        List<Stopping> stopping = [];
        foreach (var group in groups)
        {
            if (!runtimes.TryGetValue(group.Id, out var runtime))
            {
                continue;
            }

            var members = group.Members.OrderBy(m => m.Position).ToList();

            // The lowest relay that is, or is about to be, on: it runs on next, then each one above it.
            var level = 0;
            while (level < members.Count && state.IsOn(members[level].Relay!.Channel))
            {
                level++;
            }

            var low = Math.Max(level, runtime.PendingOn ? runtime.PendingPosition ?? 0 : 0);
            low = Math.Min(low, members.Count);
            var above = members.Take(Math.Max(low - 1, 0)).Sum(m => m.RunOnSec);
            var off = runtime.NextStepAtUtc is { } next
                ? Max(next, now).AddSeconds(above)
                : now.AddSeconds(above + (low > 0 ? members[low - 1].RunOnSec : 0));
            stopping.Add(new Stopping(group.Name, off));
        }

        return stopping;
    }

    private static DateTime Max(DateTime a, DateTime b)
    {
        return a > b ? a : b;
    }

    // A group that is stopping, and when its last relay goes off.
    private sealed record Stopping(string Name, DateTime Off);

    // Everything about the board, in one transaction, children before what they point to: the foreign keys from an
    // input action, a group member and a relay's runtime and schedules to a relay are Restrict, so nothing is left to a
    // cascade. Also what takes a board that couldn't be added all the way back out (BoardRegistry.AddBoardAsync).
    internal static async Task DeleteAsync(IDbContextFactory<ContactorDbContext> dbFactory, int boardId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var of = new Of(db, boardId);
        await of.InputActions.ExecuteDeleteAsync(cancellationToken);
        await of.Schedules.ExecuteDeleteAsync(cancellationToken);
        await db.GroupRuntimes.Where(r => of.GroupIds.Contains(r.GroupId)).ExecuteDeleteAsync(cancellationToken);
        await db.RelayGroupMembers.Where(m => of.GroupIds.Contains(m.GroupId) || of.RelayIds.Contains(m.RelayId)).ExecuteDeleteAsync(cancellationToken);
        await of.Groups.ExecuteDeleteAsync(cancellationToken);
        await db.RelayRuntimes.Where(r => of.RelayIds.Contains(r.RelayId)).ExecuteDeleteAsync(cancellationToken);
        await of.Relays.ExecuteDeleteAsync(cancellationToken);
        await db.Inputs.Where(i => i.BoardId == boardId).ExecuteDeleteAsync(cancellationToken);
        await db.EventLog.Where(e => e.BoardId == boardId).ExecuteDeleteAsync(cancellationToken);
        await db.BoardRuntimes.Where(r => r.BoardId == boardId).ExecuteDeleteAsync(cancellationToken);
        await KeepNextBoardsAddressAsync(db, boardId, cancellationToken);
        await db.Boards.Where(b => b.Id == boardId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Only the first board takes the address and login in its model's settings (install.sh's), which are the removed
    // board's. The board that becomes first keeps what it has: with none saved, it is saved as "no address and no
    // login", also without a model, so it never takes the removed board's at the next start.
    private static async Task KeepNextBoardsAddressAsync(ContactorDbContext db, int boardId, CancellationToken cancellationToken)
    {
        if (await db.FirstBoardIdAsync(cancellationToken) != boardId)
        {
            return;
        }

        var next = await db.Boards.Where(b => b.Id != boardId).OrderBy(b => b.SortOrder).ThenBy(b => b.Id).FirstOrDefaultAsync(cancellationToken);
        if (next is null)
        {
            return;
        }

        if (next.Host is null)
        {
            // Its model's default port, as it runs now; a board without a model yet gets the one it would be given at
            // start (BoardChoice: the VM201). Only a placeholder: without an address nothing connects.
            next.Host = "";
            next.Port = (BoardCatalogue.Find(next.Type) ?? BoardCatalogue.Find(BoardCatalogue.EarlierDefaultType))!.Model.DefaultPort;
        }

        if (next.Username is null)
        {
            next.Username = "";
            next.Password = "";
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    // What belongs to the board, as queries.
    private sealed class Of
    {
        public Of(ContactorDbContext db, int boardId)
        {
            Relays = db.Relays.Where(r => r.BoardId == boardId);
            Groups = db.RelayGroups.Where(g => g.BoardId == boardId);
            RelayIds = Relays.Select(r => r.Id);
            GroupIds = Groups.Select(g => g.Id);
            var inputIds = db.Inputs.Where(i => i.BoardId == boardId).Select(i => i.Id);
            Schedules = db.Schedules.Where(s =>
                (s.RelayId != null && RelayIds.Contains(s.RelayId.Value)) || (s.GroupId != null && GroupIds.Contains(s.GroupId.Value)));
            InputActions = db.InputActions.Where(a =>
                inputIds.Contains(a.InputId)
                || (a.RelayId != null && RelayIds.Contains(a.RelayId.Value))
                || (a.GroupId != null && GroupIds.Contains(a.GroupId.Value)));
        }

        public IQueryable<Relay> Relays { get; }

        public IQueryable<RelayGroup> Groups { get; }

        public IQueryable<int> RelayIds { get; }

        public IQueryable<int> GroupIds { get; }

        public IQueryable<Schedule> Schedules { get; }

        public IQueryable<InputAction> InputActions { get; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Relay board {BoardId} and everything about it deleted")]
    private partial void LogRemoved(int boardId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Relay board {BoardId} couldn't be deleted and doesn't run again until the app restarts")]
    private partial void LogNotRestored(Exception exception, int boardId);
}
