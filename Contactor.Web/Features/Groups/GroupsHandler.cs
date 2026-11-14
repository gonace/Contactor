using System.Text.Json;
using Contactor.Boards;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

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
public sealed record GroupCandidate(int RelayId, int Channel, string Name, bool IsActive, string? GroupName, bool HasSchedules, string? InputName = null)
{
    public bool IsFree => GroupName is null && !HasSchedules && InputName is null;

    /// <summary>"Free", "Inactive · free", "Already in Painting", "Has its own schedules" or "Controlled by Door".</summary>
    public string Note => GroupName is not null ? $"Already in {GroupName}"
        : HasSchedules ? "Has its own schedules"
        : InputName is not null ? $"Controlled by {InputName}"
        : IsActive ? "Free"
        : "Inactive · free";
}

/// <summary>
/// Creates, changes and deletes relay groups, with the rules a group must keep: 2 relays up to the board's count, each in at most one group,
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
    private readonly IRelayBoard _board;
    private readonly LocalClock _clock;
    private readonly RelayChanges _changes;
    private readonly GroupSequencer _sequencer;

    public GroupsHandler(
        IDbContextFactory<ContactorDbContext> dbFactory,
        IRelayBoard board,
        LocalClock clock,
        RelayChanges changes,
        GroupSequencer sequencer)
    {
        _dbFactory = dbFactory;
        _board = board;
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
    public async Task<IReadOnlyList<Relay>> FreeRelaysAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var taken = await db.RelayGroupMembers.Select(m => m.RelayId).ToListAsync(cancellationToken);
        var scheduled = await db.Schedules.Select(s => s.RelayId).Distinct().ToListAsync(cancellationToken);
        var controlled = await db.InputActions.Where(a => a.RelayId != null).Select(a => a.RelayId).Distinct().ToListAsync(cancellationToken);
        return await RelaysOnBoard(db)
            .Where(r => !taken.Contains(r.Id) && !scheduled.Contains(r.Id) && !controlled.Contains(r.Id))
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Channel)
            .ToListAsync(cancellationToken);
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
        var relays = await RelaysOnBoard(db).OrderBy(r => r.Channel).ToListAsync(cancellationToken);
        return [.. relays.Select(r => new GroupCandidate(
            r.Id, r.Channel, r.Name, r.IsActive, groups.GetValueOrDefault(r.Id), scheduled.Contains(r.Id), inputs.GetValueOrDefault(r.Id)))];
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

    // The board's relays, by their channels: a database can hold more, kept from a board that had more, and those
    // aren't offered.
    private IQueryable<Relay> RelaysOnBoard(ContactorDbContext db)
    {
        var count = _board.Capabilities.RelayCount;
        return db.Relays.AsNoTracking().Where(r => r.Channel <= count);
    }

    private static GroupView ToView(RelayGroup group, GroupRuntime? runtime)
    {
        return new GroupView(
            group.Id,
            group.Name,
            group.Icon,
            group.MaxOnMinutes,
            [.. group.Members.OrderBy(m => m.Position).Select(m => new GroupMemberView(m.RelayId, m.Relay!.Channel, m.Position, m.RoleName, m.StartDelaySec, m.RunOnSec))],
            runtime ?? new GroupRuntime { GroupId = group.Id });
    }

    /// <summary>Creates a group. Returns its id, or the message to show.</summary>
    public Task<(int? Id, string? Error)> CreateAsync(GroupDraft draft, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        return _sequencer.ExclusiveAsync(() => CreateCoreAsync(draft, userEmail, cancellationToken), cancellationToken);
    }

    private async Task<(int? Id, string? Error)> CreateCoreAsync(GroupDraft draft, string userEmail, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // Checked inside the transaction, so an input saved at the same moment (InputActionsHandler) can't take one of
        // its relays between the check and the save.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var relays = await RelaysOnBoard(db).ToDictionaryAsync(r => r.Id, cancellationToken);
        var error = await ValidateAsync(db, draft, relays, groupId: null, cancellationToken)
            ?? AllOff(draft.Members.Select(m => m.RelayId), relays);
        if (error is not null)
        {
            return (null, error);
        }

        var group = new RelayGroup
        {
            BoardId = await db.FirstBoardIdAsync(cancellationToken) ?? throw new InvalidOperationException("A group needs a board."),
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
    public Task<string?> SaveAsync(int groupId, GroupDraft draft, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        return _sequencer.ExclusiveAsync(() => SaveCoreAsync(groupId, draft, userEmail, cancellationToken), cancellationToken);
    }

    private async Task<string?> SaveCoreAsync(int groupId, GroupDraft draft, string userEmail, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var group = await db.RelayGroups.Include(g => g.Members).SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group is null)
        {
            return "The group no longer exists.";
        }

        // Checked inside the transaction, as when creating one.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var relays = await RelaysOnBoard(db).ToDictionaryAsync(r => r.Id, cancellationToken);
        var error = await ValidateAsync(db, draft, relays, groupId, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var before = group.Members.OrderBy(m => m.Position).Select(m => m.RelayId).ToList();
        var after = draft.Members.Select(m => m.RelayId).ToList();
        if (!before.SequenceEqual(after))
        {
            var runtime = await db.GroupRuntimes.AsNoTracking().SingleOrDefaultAsync(r => r.GroupId == groupId, cancellationToken);
            if ((runtime is not null && runtime.Phase != GroupPhases.Idle) || AllOff(before.Concat(after), relays) is not null)
            {
                return "Turn off the group, and the relays you add, before you change its relays or their order.";
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
    public Task<string?> DeleteAsync(int groupId, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        return _sequencer.ExclusiveAsync(() => DeleteCoreAsync(groupId, userEmail, cancellationToken), cancellationToken);
    }

    private async Task<string?> DeleteCoreAsync(int groupId, string userEmail, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var group = await db.RelayGroups.Include(g => g.Members).SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group is null)
        {
            return null;
        }

        var relays = await RelaysOnBoard(db).ToDictionaryAsync(r => r.Id, cancellationToken);
        var runtime = await db.GroupRuntimes.AsNoTracking().SingleOrDefaultAsync(r => r.GroupId == groupId, cancellationToken);
        if ((runtime is not null && runtime.Phase != GroupPhases.Idle) || AllOff(group.Members.Select(m => m.RelayId), relays) is not null)
        {
            return "Turn off the group before deleting it.";
        }

        db.RelayGroups.Remove(group);
        Log(db, group, userEmail, EventActions.GroupDelete, new { name = group.Name });
        await db.SaveChangesAsync(cancellationToken);
        _changes.Notify();
        return null;
    }

    private static async Task<string?> ValidateAsync(
        ContactorDbContext db,
        GroupDraft draft,
        Dictionary<int, Relay> relays,
        int? groupId,
        CancellationToken cancellationToken)
    {
        var name = draft.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            return "Give the group a name.";
        }

        if (name.Length > RelayGroup.NameMaxLength)
        {
            return $"The name can be at most {RelayGroup.NameMaxLength} characters.";
        }

        if (!GroupIcons.All.Contains(draft.Icon))
        {
            return "Choose an icon.";
        }

        if (!RelaySettingsHandler.Limits.Contains(draft.MaxOnMinutes))
        {
            return "Choose how long the group may stay on.";
        }

        if (draft.Members is null || draft.Members.Count < RelayGroup.MinMembers)
        {
            return $"Choose at least {RelayGroup.MinMembers} relays.";
        }

        // More members than the board has relays means a repeat or a relay that isn't there; both are caught here or below.
        if (draft.Members.Select(m => m.RelayId).Distinct().Count() != draft.Members.Count)
        {
            return "A relay can only be in the group once.";
        }

        foreach (var member in draft.Members)
        {
            if (!relays.TryGetValue(member.RelayId, out var relay))
            {
                return $"Relay {member.RelayId} doesn't exist.";
            }

            if ((member.RoleName?.Trim().Length ?? 0) > RelayGroupMember.RoleNameMaxLength)
            {
                return $"The name in the group can be at most {RelayGroupMember.RoleNameMaxLength} characters.";
            }

            if (member.StartDelaySec is < RelayGroupMember.MinStartDelaySec or > RelayGroupMember.MaxStartDelaySec)
            {
                return $"The wait on start is {RelayGroupMember.MinStartDelaySec}–{RelayGroupMember.MaxStartDelaySec} seconds.";
            }

            if (member.RunOnSec is < 0 or > RelayGroupMember.MaxRunOnSec)
            {
                return $"The run-on can be at most {RelayGroupMember.MaxRunOnSec / 60} minutes.";
            }

            if (await db.Schedules.AnyAsync(s => s.RelayId == member.RelayId, cancellationToken))
            {
                return $"{relay.Name} has its own schedules. Delete them first.";
            }

            var input = await db.InputActions
                .Where(a => a.RelayId == member.RelayId)
                .Join(db.Inputs, a => a.InputId, i => i.Id, (a, i) => i.Name)
                .FirstOrDefaultAsync(cancellationToken);
            if (input is not null)
            {
                return $"{relay.Name} is controlled by the input {input}. Remove it there first.";
            }
        }

        var ids = draft.Members.Select(m => m.RelayId).ToList();
        var taken = await db.RelayGroupMembers
            .Where(m => ids.Contains(m.RelayId) && m.GroupId != groupId)
            .Select(m => new { m.RelayId, Group = m.Group!.Name })
            .FirstOrDefaultAsync(cancellationToken);
        return taken is null ? null : $"{relays[taken.RelayId].Name} is already in {taken.Group}.";
    }

    // The board must say each relay is off: a group never starts with a relay running outside its order.
    private string? AllOff(IEnumerable<int> relayIds, Dictionary<int, Relay> relays)
    {
        var state = _board.State;
        if (!state.IsConnected)
        {
            return "No contact with the relay board. It's needed to see that the relays are off.";
        }

        // By channel; a relay the board doesn't have can't be on.
        var on = relayIds.Distinct().Select(relays.GetValueOrDefault).FirstOrDefault(r => r is not null && state.IsOn(r.Channel));
        return on is null ? null : $"Turn off {on.Name} first.";
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
