using System.Text.Json;
using Contactor.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Contactor.Web.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Contactor.Web.Features.Groups;

/// <summary>A group as it is created or saved; the members in start order.</summary>
public sealed record GroupDraft(string Name, string Icon, int? MaxOnMinutes, IReadOnlyList<GroupMemberDraft> Members);

/// <summary>A relay in a <see cref="GroupDraft"/>. An empty role name takes the relay's own name.</summary>
public sealed record GroupMemberDraft(
    int RelayId,
    string? RoleName,
    int StartDelaySec = RelayGroupMember.DefaultStartDelaySec,
    int RunOnSec = RelayGroupMember.DefaultRunOnSec);

/// <summary>A relay as "New group" offers it.</summary>
/// <param name="GroupName">The group it is already in, or null.</param>
/// <param name="HasSchedules">It has schedules of its own, so it can't join a group.</param>
/// <param name="InputName">An input that controls it, so it can't join a group; null when none does.</param>
/// <param name="Channel">Its channel on the board, the number the screens show.</param>
/// <param name="BoardId">Its board: a group's relays are all on one board.</param>
public sealed record GroupCandidate(int RelayId, int Channel, string Name, bool IsActive, string? GroupName, bool HasSchedules, string? InputName = null, int BoardId = 0)
{
    public bool IsFree => GroupName is null && !HasSchedules && InputName is null;
}

/// <summary>
/// Creates, changes and deletes relay groups, with the rules a group must keep: 2 relays up to the board's count, all on
/// one board (the group's: a new group takes its first relay's; across boards is issue #204), each in at most one group,
/// none with schedules of its own, and the waits within their limits. Which relays are in it and their order only
/// change while the whole group is off, so no running sequence is pulled apart; the same goes for creating and
/// deleting. Every change is logged.
/// </summary>
public sealed class GroupsHandler
{
    /// <summary>The run-on times the screens offer, in seconds: none, 1, 3, 5, 10 and 15 min.</summary>
    public static readonly IReadOnlyList<int> RunOnChoices = [0, 60, 180, 300, 600, 900];

    /// <summary>The waits the screens offer before a relay starts, in seconds.</summary>
    public static readonly IReadOnlyList<int> StartDelayChoices = [1, 3, 10, 30, 60];

    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly BoardRegistry _registry;
    private readonly LocalClock _clock;
    private readonly RelayChanges _changes;
    private readonly GroupSequencer _sequencer;
    private readonly IStringLocalizer<GroupsTexts> _texts;

    public GroupsHandler(
        IDbContextFactory<ContactorDbContext> dbFactory,
        BoardRegistry registry,
        LocalClock clock,
        RelayChanges changes,
        GroupSequencer sequencer,
        IStringLocalizer<GroupsTexts> texts)
    {
        _texts = texts;
        _dbFactory = dbFactory;
        _registry = registry;
        _clock = clock;
        _changes = changes;
        _sequencer = sequencer;
    }

    /// <summary>Every group with its members in start order and its runtime, for the screens.</summary>
    public async Task<IReadOnlyList<GroupView>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var groups = await db.RelayGroups.AsNoTracking().Include(g => g.Members).ThenInclude(m => m.Relay).OrderBy(g => g.SortOrder).ThenBy(g => g.Id).ToListAsync(cancellationToken);
        var runtimes = await db.GroupRuntimes.AsNoTracking().ToDictionaryAsync(r => r.GroupId, cancellationToken);
        return [.. groups.Select(g => ToView(g, runtimes.GetValueOrDefault(g.Id)))];
    }

    /// <summary>One group, or null when it doesn't exist.</summary>
    public async Task<GroupView?> GetAsync(int groupId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var group = await db.RelayGroups.AsNoTracking().Include(g => g.Members).ThenInclude(m => m.Relay).SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group is null)
        {
            return null;
        }

        var runtime = await db.GroupRuntimes.AsNoTracking().SingleOrDefaultAsync(r => r.GroupId == groupId, cancellationToken);
        return ToView(group, runtime);
    }

    /// <summary>The relays that can join a group: in none, without schedules of their own, and controlled by no input.</summary>
    public Task<IReadOnlyList<Relay>> FreeRelaysAsync(CancellationToken cancellationToken)
    {
        return FreeRelaysAsync(null, cancellationToken);
    }

    /// <summary>
    /// As <see cref="FreeRelaysAsync(CancellationToken)"/>; with <paramref name="boardId"/>, only that board's (a group's
    /// relays are all on its board).
    /// </summary>
    public async Task<IReadOnlyList<Relay>> FreeRelaysAsync(int? boardId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var taken = await db.RelayGroupMembers.Select(m => m.RelayId).ToListAsync(cancellationToken);
        var scheduled = await db.Schedules.Select(s => s.RelayId).Distinct().ToListAsync(cancellationToken);
        var controlled = await db.InputActions.Where(a => a.RelayId != null).Select(a => a.RelayId).Distinct().ToListAsync(cancellationToken);
        return [.. (await db.Relays.AsNoTracking()
                .Where(r => boardId == null || r.BoardId == boardId)
                .Where(r => !taken.Contains(r.Id) && !scheduled.Contains(r.Id) && !controlled.Contains(r.Id))
                .OrderBy(r => r.SortOrder).ThenBy(r => r.Channel)
                .ToListAsync(cancellationToken))
            .Where(IsOnBoard)];
    }

    /// <summary>Every relay, and whether it can join a new group, for "New group".</summary>
    public async Task<IReadOnlyList<GroupCandidate>> CandidatesAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var groups = await db.RelayGroupMembers.AsNoTracking()
            .Select(m => new { m.RelayId, m.Group!.Name })
            .ToDictionaryAsync(m => m.RelayId, m => m.Name, cancellationToken);
        var scheduled = (await db.Schedules.Select(s => s.RelayId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var inputs = await ControllingInputsAsync(db, cancellationToken);
        var relays = await RelaysOnBoardsAsync(db, cancellationToken);
        return [.. relays.Values.OrderBy(r => r.BoardId).ThenBy(r => r.Channel).Select(r => new GroupCandidate(
            r.Id, r.Channel, r.Name, r.IsActive, groups.GetValueOrDefault(r.Id), scheduled.Contains(r.Id), inputs.GetValueOrDefault(r.Id), r.BoardId))];
    }

    // The name of an input that controls each relay; the first by number (its channel) when several do.
    private static async Task<Dictionary<int, string>> ControllingInputsAsync(ContactorDbContext db, CancellationToken cancellationToken)
    {
        var actions = await db.InputActions.AsNoTracking()
            .Where(a => a.RelayId != null)
            .Join(db.Inputs, a => a.InputId, i => i.Id, (a, i) => new { RelayId = a.RelayId!.Value, i.Channel, i.Name })
            .ToListAsync(cancellationToken);
        return actions.GroupBy(a => a.RelayId).ToDictionary(g => g.Key, g => g.OrderBy(a => a.Channel).First().Name);
    }

    // Every relay its own board has, by its id: a database can hold more, kept from a board that had more, and those
    // aren't offered.
    private async Task<Dictionary<int, Relay>> RelaysOnBoardsAsync(ContactorDbContext db, CancellationToken cancellationToken)
    {
        return (await db.Relays.AsNoTracking().ToListAsync(cancellationToken)).Where(IsOnBoard).ToDictionary(r => r.Id);
    }

    private bool IsOnBoard(Relay relay)
    {
        return _registry.HasRelay(relay.BoardId, relay.Channel);
    }

    private static GroupView ToView(RelayGroup group, GroupRuntime? runtime)
    {
        return new GroupView(
            group.Id,
            group.Name,
            group.Icon,
            group.MaxOnMinutes,
            [.. group.Members.OrderBy(m => m.Position).Select(m => new GroupMemberView(m.RelayId, m.Relay!.Channel, m.Position, m.RoleName, m.StartDelaySec, m.RunOnSec))],
            runtime ?? new GroupRuntime { GroupId = group.Id })
        {
            BoardId = group.BoardId,
        };
    }

    /// <summary>
    /// Creates a group on its first relay's board, under that board's lock. Returns its id, or the message to show.
    /// </summary>
    public async Task<(int? Id, string? Error)> CreateAsync(GroupDraft draft, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        int? boardId = null;
        if (draft.Members is [var first, ..])
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            boardId = await db.Relays.Where(r => r.Id == first.RelayId).Select(r => (int?)r.BoardId).SingleOrDefaultAsync(cancellationToken);
        }

        // Without a first relay that exists the draft is refused before anything else.
        if (boardId is not { } id)
        {
            return await CreateCoreAsync(draft, userEmail, boardId: null, cancellationToken);
        }

        return await _sequencer.ExclusiveAsync(id, () => CreateCoreAsync(draft, userEmail, id, cancellationToken), cancellationToken);
    }

    private async Task<(int? Id, string? Error)> CreateCoreAsync(GroupDraft draft, string userEmail, int? boardId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // Checked inside the transaction, so an input saved at the same moment (InputActionsHandler) can't take one of
        // its relays between the check and the save.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var relays = await RelaysOnBoardsAsync(db, cancellationToken);
        var error = await ValidateAsync(db, draft, relays, groupId: null, boardId, cancellationToken)
            ?? AllOff(boardId, draft.Members.Select(m => m.RelayId), relays);
        if (error is not null)
        {
            return (null, error);
        }

        var group = new RelayGroup
        {
            BoardId = boardId ?? throw new InvalidOperationException("A group needs a board."),
            Name = draft.Name.Trim(),
            Icon = draft.Icon,
            MaxOnMinutes = draft.MaxOnMinutes,
            SortOrder = await db.RelayGroups.CountAsync(cancellationToken) + 1,
        };
        AddMembers(group, draft, relays);
        db.RelayGroups.Add(group);
        await db.SaveChangesAsync(cancellationToken);

        db.GroupRuntimes.Add(new GroupRuntime { GroupId = group.Id });
        await ForgetRelayRunsAsync(db, group.Members.Select(m => m.RelayId), cancellationToken);
        Log(db, group, userEmail, EventActions.GroupCreate, new { name = group.Name, steps = group.Members.Select(m => m.RoleName) });
        await RelayRuns.SaveAsync(db, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _changes.Notify();
        return (group.Id, null);
    }

    /// <summary>
    /// Saves a group. The name, icon, auto-off, role names and waits can change any time; which relays and their order
    /// only while the whole group is off. Returns null when saved, otherwise the message to show.
    /// </summary>
    public async Task<string?> SaveAsync(int groupId, GroupDraft draft, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        if (await BoardOfAsync(groupId, cancellationToken) is not { } boardId)
        {
            return _texts["Groups.Gone"];
        }

        return await _sequencer.ExclusiveAsync(boardId, () => SaveCoreAsync(groupId, draft, userEmail, cancellationToken), cancellationToken);
    }

    // The group's board, or null when the group doesn't exist. A group's board never changes.
    private async Task<int?> BoardOfAsync(int groupId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.RelayGroups.Where(g => g.Id == groupId).Select(g => (int?)g.BoardId).SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<string?> SaveCoreAsync(int groupId, GroupDraft draft, string userEmail, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var group = await db.RelayGroups.Include(g => g.Members).SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group is null)
        {
            return _texts["Groups.Gone"];
        }

        // Checked inside the transaction, as when creating one.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var relays = await RelaysOnBoardsAsync(db, cancellationToken);
        var error = await ValidateAsync(db, draft, relays, groupId, group.BoardId, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var before = group.Members.OrderBy(m => m.Position).Select(m => m.RelayId).ToList();
        var after = draft.Members.Select(m => m.RelayId).ToList();
        if (!before.SequenceEqual(after))
        {
            var runtime = await db.GroupRuntimes.AsNoTracking().SingleOrDefaultAsync(r => r.GroupId == groupId, cancellationToken);
            if ((runtime is not null && runtime.Phase != GroupPhases.Idle) || AllOff(group.BoardId, before.Concat(after), relays) is not null)
            {
                return _texts["Groups.Save.TurnOffFirst"];
            }

            // Replaced in two saves: the unique position per group would clash while rows move.
            db.RelayGroupMembers.RemoveRange(group.Members);
            await db.SaveChangesAsync(cancellationToken);
            group.Members.Clear();
            AddMembers(group, draft, relays);
            await ForgetRelayRunsAsync(db, after.Except(before), cancellationToken);
        }
        else
        {
            foreach (var (member, change) in group.Members.OrderBy(m => m.Position).Zip(draft.Members))
            {
                member.RoleName = RoleName(change, relays);
                member.StartDelaySec = change.StartDelaySec;
                member.RunOnSec = change.RunOnSec;
            }
        }

        group.Name = draft.Name.Trim();
        group.Icon = draft.Icon;
        group.MaxOnMinutes = draft.MaxOnMinutes;
        Log(db, group, userEmail, EventActions.GroupEdit, new { name = group.Name });
        await RelayRuns.SaveAsync(db, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _changes.Notify();
        return null;
    }

    /// <summary>
    /// Deletes a group while it is off; its relays are ordinary relays again. Returns null when deleted, otherwise the
    /// message to show.
    /// </summary>
    public async Task<string?> DeleteAsync(int groupId, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        if (await BoardOfAsync(groupId, cancellationToken) is not { } boardId)
        {
            return null;
        }

        return await _sequencer.ExclusiveAsync(boardId, () => DeleteCoreAsync(groupId, userEmail, cancellationToken), cancellationToken);
    }

    private async Task<string?> DeleteCoreAsync(int groupId, string userEmail, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var group = await db.RelayGroups.Include(g => g.Members).SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group is null)
        {
            return null;
        }

        var relays = await RelaysOnBoardsAsync(db, cancellationToken);
        var runtime = await db.GroupRuntimes.AsNoTracking().SingleOrDefaultAsync(r => r.GroupId == groupId, cancellationToken);
        if ((runtime is not null && runtime.Phase != GroupPhases.Idle) || AllOff(group.BoardId, group.Members.Select(m => m.RelayId), relays) is not null)
        {
            return _texts["Groups.Delete.TurnOffFirst"];
        }

        db.RelayGroups.Remove(group);
        Log(db, group, userEmail, EventActions.GroupDelete, new { name = group.Name });
        await db.SaveChangesAsync(cancellationToken);
        _changes.Notify();
        return null;
    }

    private async Task<string?> ValidateAsync(
        ContactorDbContext db,
        GroupDraft draft,
        Dictionary<int, Relay> relays,
        int? groupId,
        int? boardId,
        CancellationToken cancellationToken)
    {
        var name = draft.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            return _texts["Groups.Save.Name"];
        }

        if (name.Length > RelayGroup.NameMaxLength)
        {
            return _texts["Groups.Save.NameTooLong", RelayGroup.NameMaxLength];
        }

        if (!GroupIcons.All.Contains(draft.Icon))
        {
            return _texts["Groups.Save.Icon"];
        }

        if (!RelaySettingsHandler.Limits.Contains(draft.MaxOnMinutes))
        {
            return _texts["Groups.Save.Limit"];
        }

        if (draft.Members is null || draft.Members.Count < RelayGroup.MinMembers)
        {
            return _texts["Groups.Save.TooFew", RelayGroup.MinMembers];
        }

        // More members than the board has relays means a repeat or a relay that isn't there; both are caught here or below.
        if (draft.Members.Select(m => m.RelayId).Distinct().Count() != draft.Members.Count)
        {
            return _texts["Groups.Save.Twice"];
        }

        foreach (var member in draft.Members)
        {
            if (!relays.TryGetValue(member.RelayId, out var relay))
            {
                return _texts["Groups.Save.NoRelay"];
            }

            // One board: the group's order and interlock are checked on each of its board's statuses, and the board
            // switches them; relays on two boards can't be kept in order (across boards is issue #204).
            if (relay.BoardId != boardId)
            {
                return _texts["Groups.Save.OneBoard"];
            }

            if ((member.RoleName?.Trim().Length ?? 0) > RelayGroupMember.RoleNameMaxLength)
            {
                return _texts["Groups.Save.RoleTooLong", RelayGroupMember.RoleNameMaxLength];
            }

            if (member.StartDelaySec is < RelayGroupMember.MinStartDelaySec or > RelayGroupMember.MaxStartDelaySec)
            {
                return _texts["Groups.Save.Wait", RelayGroupMember.MinStartDelaySec, RelayGroupMember.MaxStartDelaySec];
            }

            if (member.RunOnSec is < 0 or > RelayGroupMember.MaxRunOnSec)
            {
                return _texts["Groups.Save.RunOn", RelayGroupMember.MaxRunOnSec / 60];
            }

            if (await db.Schedules.AnyAsync(s => s.RelayId == member.RelayId, cancellationToken))
            {
                return _texts["Groups.Save.HasSchedules", relay.Name];
            }

            var input = await db.InputActions
                .Where(a => a.RelayId == member.RelayId)
                .Join(db.Inputs, a => a.InputId, i => i.Id, (a, i) => i.Name)
                .FirstOrDefaultAsync(cancellationToken);
            if (input is not null)
            {
                return _texts["Groups.Save.Controlled", relay.Name, input];
            }
        }

        var ids = draft.Members.Select(m => m.RelayId).ToList();
        var taken = await db.RelayGroupMembers
            .Where(m => ids.Contains(m.RelayId) && m.GroupId != groupId)
            .Select(m => new { m.RelayId, Group = m.Group!.Name })
            .FirstOrDefaultAsync(cancellationToken);
        return taken is null ? null : _texts["Groups.Save.InGroup", relays[taken.RelayId].Name, taken.Group].Value;
    }

    // The group's board must say each relay is off: a group never starts with a relay running outside its order.
    private string? AllOff(int? boardId, IEnumerable<int> relayIds, Dictionary<int, Relay> relays)
    {
        var state = boardId is { } id ? _registry.Find(id)?.State : null;
        if (state is not { IsConnected: true })
        {
            return _texts["Groups.Save.NoContact"];
        }

        // By channel on the group's board; a relay the board doesn't have can't be on, and the relays are all on it.
        var on = relayIds.Distinct().Select(relays.GetValueOrDefault).FirstOrDefault(r => r is not null && r.BoardId == boardId && state.IsOn(r.Channel));
        return on is null ? null : _texts["Groups.Save.RelayOn", on.Name].Value;
    }

    private static void AddMembers(RelayGroup group, GroupDraft draft, Dictionary<int, Relay> relays)
    {
        var position = 1;
        foreach (var member in draft.Members)
        {
            group.Members.Add(new RelayGroupMember
            {
                RelayId = member.RelayId,
                Position = position++,
                RoleName = RoleName(member, relays),
                StartDelaySec = member.StartDelaySec,
                RunOnSec = member.RunOnSec,
            });
        }
    }

    private static string RoleName(GroupMemberDraft member, Dictionary<int, Relay> relays)
    {
        var name = member.RoleName?.Trim();
        return string.IsNullOrEmpty(name) ? relays[member.RelayId].Name : name;
    }

    // A relay in a group has no run of its own: no auto-off, no "turned on by".
    private static async Task ForgetRelayRunsAsync(ContactorDbContext db, IEnumerable<int> relayIds, CancellationToken cancellationToken)
    {
        var ids = relayIds.ToList();
        foreach (var relayId in await db.RelayRuntimes.Where(r => ids.Contains(r.RelayId)).Select(r => r.RelayId).ToListAsync(cancellationToken))
        {
            await RelayRuns.ClearAsync(db, relayId, cancellationToken);
        }
    }

    private void Log(ContactorDbContext db, RelayGroup group, string userEmail, string action, object details)
    {
        db.EventLog.Add(new EventLogEntry
        {
            TimestampUtc = _clock.UtcNow,
            BoardId = group.BoardId,
            GroupId = group.Id,
            Actor = userEmail,
            Action = action,
            Details = JsonSerializer.Serialize(details),
        });
    }
}
