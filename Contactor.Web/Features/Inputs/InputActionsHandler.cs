using Contactor.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Inputs;

/// <summary>A relay or a group an input can control: exactly one of <paramref name="RelayId"/> and <paramref name="GroupId"/>.</summary>
/// <param name="Channel">A relay's channel on the board, the number the screens show; null for a group.</param>
public sealed record InputActionTarget(int? RelayId, int? Channel, int? GroupId, string Name)
{
    /// <summary>"Group: Fan heater" or "Relay 4: Lighting", by the relay's channel.</summary>
    public string Label => GroupId is not null ? $"Group: {Name}" : $"Relay {Channel}: {Name}";

    /// <summary>
    /// Tells relays and groups apart on a page ("group:2", "relay:4"): names aren't unique, so two groups with one name
    /// must still have different keys.
    /// </summary>
    public string Key => GroupId is { } groupId ? $"group:{groupId}" : $"relay:{RelayId}";

    /// <summary>Whether it is the same relay or group.</summary>
    public bool IsSame(InputActionTarget other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return RelayId == other.RelayId && GroupId == other.GroupId;
    }
}

/// <summary>
/// What an input does to one relay or group. <paramref name="WhenClosed"/> and <paramref name="WhenOpened"/>: true turns
/// it on, false turns it off, null does nothing.
/// </summary>
public sealed record InputActionRow(InputActionTarget Target, bool? WhenClosed, bool? WhenOpened);

/// <summary>
/// The relays and groups an input controls, on the Input screen. A relay in a group is only controlled through its
/// group, and a relay an input controls can't join a group (<see cref="Groups.GroupsHandler"/>), as with a relay's
/// own schedules. An input only controls relays and groups on its own board (across boards is issue #204).
/// </summary>
public sealed class InputActionsHandler
{
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly BoardRegistry _registry;
    private readonly RelayChanges _changes;

    public InputActionsHandler(IDbContextFactory<ContactorDbContext> dbFactory, BoardRegistry registry, RelayChanges changes)
    {
        _dbFactory = dbFactory;
        _registry = registry;
        _changes = changes;
    }

    /// <summary>
    /// What the input controls: its groups first, then its relays, as on the Relays screen. A relay its board doesn't
    /// have (above its count, left from a board that had more) is kept but not shown.
    /// </summary>
    public async Task<IReadOnlyList<InputActionRow>> ListAsync(int inputId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var actions = (await db.InputActions.AsNoTracking()
            .Where(a => a.InputId == inputId)
            .Select(a => new
            {
                a.RelayId,
                RelayBoardId = a.Relay == null ? (int?)null : a.Relay.BoardId,
                Channel = a.Relay == null ? (int?)null : a.Relay.Channel,
                a.GroupId,
                RelayName = a.Relay == null ? null : a.Relay.Name,
                GroupName = a.Group == null ? null : a.Group.Name,
                GroupOrder = a.Group == null ? 0 : a.Group.SortOrder,
                a.WhenClosed,
                a.WhenOpened,
            })
            .ToListAsync(cancellationToken))
            .Where(a => a.RelayId is null || _registry.HasRelay(a.RelayBoardId!.Value, a.Channel!.Value));
        return [.. actions
            .OrderBy(a => a.GroupId is null)
            .ThenBy(a => a.GroupOrder)
            .ThenBy(a => a.GroupId)
            .ThenBy(a => a.Channel)
            .Select(a => new InputActionRow(
                new InputActionTarget(a.RelayId, a.Channel, a.GroupId, a.GroupName ?? a.RelayName ?? ""),
                a.WhenClosed,
                a.WhenOpened))];
    }

    /// <summary>
    /// Every group of the input's board, then every relay on its board that is in no group: what the input may control.
    /// None for an input that doesn't exist.
    /// </summary>
    public async Task<IReadOnlyList<InputActionTarget>> TargetsAsync(int inputId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Inputs.Where(i => i.Id == inputId).Select(i => (int?)i.BoardId).SingleOrDefaultAsync(cancellationToken) is { } boardId
            ? await TargetsAsync(db, boardId, cancellationToken)
            : [];
    }

    /// <summary>
    /// Replaces what the input controls. Not logged, like the rest of the Input screen. Returns null when saved,
    /// otherwise the message to show.
    /// </summary>
    public async Task<string?> SaveAsync(int inputId, IReadOnlyList<InputActionRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // Checked inside the transaction, so a group saved at the same moment (GroupsHandler) can't take a relay
        // between the check and the save.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await CheckAsync(db, inputId, rows, cancellationToken) is { } error)
        {
            return error;
        }

        // What isn't shown, a relay its board doesn't have, stays.
        db.InputActions.RemoveRange((await db.InputActions
            .Include(a => a.Relay)
            .Where(a => a.InputId == inputId)
            .ToListAsync(cancellationToken))
            .Where(a => a.Relay is null || _registry.HasRelay(a.Relay.BoardId, a.Relay.Channel)));
        db.InputActions.AddRange(rows.Select(r => new InputAction
        {
            InputId = inputId,
            RelayId = r.Target.RelayId,
            GroupId = r.Target.GroupId,
            WhenClosed = r.WhenClosed,
            WhenOpened = r.WhenOpened,
        }));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _changes.Notify();
        return null;
    }

    /// <summary>
    /// What <see cref="SaveAsync"/> would refuse, without saving anything, so the Input screen can check the relays and
    /// groups before it saves the name. Null when they can be saved.
    /// </summary>
    public async Task<string?> CheckAsync(int inputId, IReadOnlyList<InputActionRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await CheckAsync(db, inputId, rows, cancellationToken);
    }

    private async Task<string?> CheckAsync(ContactorDbContext db, int inputId, IReadOnlyList<InputActionRow> rows, CancellationToken cancellationToken)
    {
        var input = await db.Inputs.AsNoTracking().Where(i => i.Id == inputId).Select(i => new { i.BoardId, i.Channel }).SingleOrDefaultAsync(cancellationToken);
        if (input is null || !_registry.HasInput(input.BoardId, input.Channel))
        {
            return "The input doesn't exist.";
        }

        // Checked against the database, not the page: a relay may have joined a group since the page was opened. Only
        // the input's own board's.
        var targets = await TargetsAsync(db, input.BoardId, cancellationToken);
        var seen = new List<InputActionTarget>();
        foreach (var row in rows)
        {
            ArgumentNullException.ThrowIfNull(row?.Target);
            if (!targets.Any(t => t.IsSame(row.Target)))
            {
                return await WhyNotAsync(db, input.BoardId, row.Target, cancellationToken);
            }

            if (seen.Any(t => t.IsSame(row.Target)))
            {
                return $"{row.Target.Label} is in the list twice.";
            }

            seen.Add(row.Target);
        }

        return null;
    }

    // Why a relay or group isn't one the input may control. By the relay's channel, as the screens number it.
    private async Task<string> WhyNotAsync(ContactorDbContext db, int boardId, InputActionTarget target, CancellationToken cancellationToken)
    {
        if (target.GroupId is { } groupId)
        {
            var groupBoard = await db.RelayGroups.Where(g => g.Id == groupId).Select(g => (int?)g.BoardId).SingleOrDefaultAsync(cancellationToken);
            return groupBoard is { } other && other != boardId ? "That group is on another board: an input only controls its own board's." : "That group no longer exists.";
        }

        var relay = target.RelayId is { } relayId
            ? await db.Relays.AsNoTracking().Where(r => r.Id == relayId).Select(r => new { r.BoardId, r.Channel }).SingleOrDefaultAsync(cancellationToken)
            : null;
        if (relay is not null && relay.BoardId != boardId)
        {
            return "That relay is on another board: an input only controls its own board's.";
        }

        return relay is not null && _registry.HasRelay(relay.BoardId, relay.Channel) ? $"Relay {relay.Channel} is in a group now: add the group instead."
            : target.Channel is { } shown ? $"The board has no relay {shown}."
            : "The board has no such relay.";
    }

    // The board's groups, then its relays in no group that it has (by their channels).
    private async Task<IReadOnlyList<InputActionTarget>> TargetsAsync(ContactorDbContext db, int boardId, CancellationToken cancellationToken)
    {
        var groups = await db.RelayGroups.AsNoTracking()
            .Where(g => g.BoardId == boardId)
            .OrderBy(g => g.SortOrder).ThenBy(g => g.Id)
            .Select(g => new InputActionTarget(null, null, g.Id, g.Name))
            .ToListAsync(cancellationToken);
        var members = db.RelayGroupMembers.Select(m => m.RelayId);
        var relays = (await db.Relays.AsNoTracking()
            .Where(r => r.BoardId == boardId && !members.Contains(r.Id))
            .OrderBy(r => r.Channel)
            .ToListAsync(cancellationToken))
            .Where(r => _registry.HasRelay(boardId, r.Channel))
            .Select(r => new InputActionTarget(r.Id, r.Channel, null, r.Name));
        return [.. groups, .. relays];
    }
}
