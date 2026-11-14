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
/// Takes a board out of the app, on its page in Settings. A board that answers has its relays turned off first: "All
/// off" for that board (<see cref="RelayControl.TurnAllOffAsync"/>), its groups from the bottom without waiting out
/// their run-on times, until the board shows every relay off. A board without contact is removed as it is: whatever is
/// on stays on. Then its live board is stopped and let go (<see cref="BoardRegistry.RemoveAsync"/>), so nothing acts on
/// it any more, and everything about it is deleted in one transaction, its events included. The board itself isn't
/// changed. The last board can be removed too: the app then has no board, as a new installation before the setup guide
/// chooses one.
/// </summary>
public sealed partial class BoardRemoval
{
    /// <summary>How long the board has to show every relay off before removing it is given up.</summary>
    public static readonly TimeSpan OffWait = TimeSpan.FromSeconds(30);

    /// <summary>How often the board's relays are looked at meanwhile.</summary>
    public static readonly TimeSpan OffCheck = TimeSpan.FromMilliseconds(500);

    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly BoardRegistry _registry;
    private readonly RelayControl _control;
    private readonly GroupSequencer _groups;
    private readonly RelayChanges _changes;
    private readonly TimeProvider _time;
    private readonly ILogger<BoardRemoval> _logger;

    public BoardRemoval(
        IDbContextFactory<ContactorDbContext> dbFactory,
        BoardRegistry registry,
        RelayControl control,
        GroupSequencer groups,
        RelayChanges changes,
        TimeProvider time,
        ILogger<BoardRemoval> logger)
    {
        _dbFactory = dbFactory;
        _registry = registry;
        _control = control;
        _groups = groups;
        _changes = changes;
        _time = time;
        _logger = logger;
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
    /// turned off first, so a board that has lost contact since isn't removed then. A board that answers always has
    /// its relays turned off first. Returns null when removed (or gone already), otherwise the message to show; nothing
    /// is removed then.
    /// </summary>
    public async Task<string?> RemoveAsync(int boardId, bool turnOffFirst, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        string? name;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            name = await db.Boards.Where(b => b.Id == boardId).Select(b => b.Name).SingleOrDefaultAsync(cancellationToken);
        }

        if (name is null)
        {
            // Someone else removed it meanwhile.
            await _registry.RemoveAsync(boardId, cancellationToken);
            return null;
        }

        var live = _registry.Find(boardId);
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
        try
        {
            await DeleteAsync(boardId, cancellationToken);
        }
        catch
        {
            // Nothing was deleted: the board runs again, as at start.
            try
            {
                await _registry.LoadAsync(CancellationToken.None);
            }
#pragma warning disable CA1031 // The failure to delete is the one thrown; this one is logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogNotRestored(ex, boardId);
            }

            throw;
        }

        LogRemoved(boardId);
        _changes.Notify();
        return null;
    }

    private static string LostText(string name)
    {
        return $"{name} has lost contact, so its relays couldn't be turned off. Nothing was removed.";
    }

    // "All off" for the board, then its groups' run-on times cut short, until the board shows every relay off. Returns
    // why not, and whether that was a lost contact.
    private async Task<(string? Error, bool Lost)> TurnOffAsync(LiveBoard live, string name, string userEmail, CancellationToken cancellationToken)
    {
        var boardId = live.BoardId;
        try
        {
            var all = live.Capabilities.AllRelays;
            var single = all.Except(await _groups.MemberMaskAsync(boardId, cancellationToken));
            await _control.TurnAllOffAsync(boardId, userEmail, cancellationToken);
            var deadline = _time.GetUtcNow() + OffWait;
            while (true)
            {
                var state = live.State;
                if (!state.IsConnected)
                {
                    return (LostText(name), true);
                }

                if ((state.Relays & all).IsEmpty)
                {
                    return (null, false);
                }

                if (_time.GetUtcNow() >= deadline)
                {
                    return ($"Some of {name}'s relays didn't turn off. Nothing was removed. Try again.", false);
                }

                // A relay outside a group that is on again (a schedule, an input) gets "All off" once more; the groups
                // go on stopping from the bottom, each relay once the one below is confirmed off.
                if (!(state.Relays & single).IsEmpty)
                {
                    await _control.TurnAllOffAsync(boardId, userEmail, cancellationToken);
                }
                else
                {
                    await _groups.EndRunOnsAsync(boardId, cancellationToken);
                }

                await Task.Delay(OffCheck, _time, cancellationToken);
            }
        }
        catch (BoardNotConnectedException)
        {
            return (LostText(name), true);
        }
    }

    // Everything about the board, in one transaction, children before what they point to: the foreign keys from an
    // input action, a group member and a relay's runtime and schedules to a relay are Restrict, so nothing is left to a
    // cascade.
    private async Task DeleteAsync(int boardId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
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
    // login", so it never takes the removed board's at the next start.
    private async Task KeepNextBoardsAddressAsync(ContactorDbContext db, int boardId, CancellationToken cancellationToken)
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

        if (next.Host is null && _registry.Find(next.Id)?.DefaultEndpoint is { Port: > 0 } running)
        {
            next.Host = "";
            next.Port = running.Port;
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
