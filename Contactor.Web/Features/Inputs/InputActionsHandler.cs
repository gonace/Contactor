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
/// own schedules.
/// </summary>
public sealed class InputActionsHandler
{
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly OnlyBoard _board;
    private readonly RelayChanges _changes;

    public InputActionsHandler(IDbContextFactory<ContactorDbContext> dbFactory, BoardRegistry registry, RelayChanges changes)
    {
        _dbFactory = dbFactory;
        // Until PR 4 (Issue #4): one board.
        _board = registry.Only;
        _changes = changes;
    }

    /// <summary>
    /// What the input controls: its groups first, then its relays, as on the Relays screen. A relay above the board's
    /// count, left from a board that had more, is kept but not shown.
    /// </summary>
    public async Task<IReadOnlyList<InputActionRow>> ListAsync(int inputId, CancellationToken cancellationToken)
    {
        var count = _board.Capabilities.RelayCount;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var actions = await db.InputActions.AsNoTracking()
            .Where(a => a.InputId == inputId && (a.RelayId == null || a.Relay!.Channel <= count))
            .Select(a => new
            {
                a.RelayId,
                Channel = a.Relay == null ? (int?)null : a.Relay.Channel,
                a.GroupId,
                RelayName = a.Relay == null ? null : a.Relay.Name,
                GroupName = a.Group == null ? null : a.Group.Name,
                GroupOrder = a.Group == null ? 0 : a.Group.SortOrder,
                a.WhenClosed,
                a.WhenOpened,
            })
            .ToListAsync(cancellationToken);
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

    /// <summary>Every group, then every relay on the board that is in no group: what an input may control.</summary>
    public async Task<IReadOnlyList<InputActionTarget>> TargetsAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await TargetsAsync(db, cancellationToken);
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

        // What isn't shown, a relay above the board's count, stays.
        var count = _board.Capabilities.RelayCount;
        db.InputActions.RemoveRange(await db.InputActions
            .Where(a => a.InputId == inputId && (a.RelayId == null || a.Relay!.Channel <= count))
            .ToListAsync(cancellationToken));
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
        var inputCount = _board.Capabilities.InputCount;
        if (!await db.Inputs.AnyAsync(i => i.Id == inputId && i.Channel <= inputCount, cancellationToken))
        {
            return "The input doesn't exist.";
        }

        // Checked against the database, not the page: a relay may have joined a group since the page was opened.
        var targets = await TargetsAsync(db, cancellationToken);
        var seen = new List<InputActionTarget>();
        foreach (var row in rows)
        {
            ArgumentNullException.ThrowIfNull(row?.Target);
            if (!targets.Any(t => t.IsSame(row.Target)))
            {
                // By the relay's channel, as the screens number it.
                var count = _board.Capabilities.RelayCount;
                var channel = row.Target.RelayId is { } relayId
                    ? await db.Relays.Where(r => r.Id == relayId && r.Channel <= count).Select(r => (int?)r.Channel).SingleOrDefaultAsync(cancellationToken)
                    : null;
                return row.Target.GroupId is not null ? "That group no longer exists."
                    : channel is not null ? $"Relay {channel} is in a group now: add the group instead."
                    : row.Target.Channel is { } shown ? $"The board has no relay {shown}."
                    : "The board has no such relay.";
            }

            if (seen.Any(t => t.IsSame(row.Target)))
            {
                return $"{row.Target.Label} is in the list twice.";
            }

            seen.Add(row.Target);
        }

        return null;
    }

    private async Task<IReadOnlyList<InputActionTarget>> TargetsAsync(ContactorDbContext db, CancellationToken cancellationToken)
    {
        var count = _board.Capabilities.RelayCount;
        var groups = await db.RelayGroups.AsNoTracking()
            .OrderBy(g => g.SortOrder).ThenBy(g => g.Id)
            .Select(g => new InputActionTarget(null, null, g.Id, g.Name))
            .ToListAsync(cancellationToken);
        var members = db.RelayGroupMembers.Select(m => m.RelayId);
        var relays = await db.Relays.AsNoTracking()
            .Where(r => r.Channel <= count && !members.Contains(r.Id))
            .OrderBy(r => r.Channel)
            .Select(r => new InputActionTarget(r.Id, r.Channel, null, r.Name))
            .ToListAsync(cancellationToken);
        return [.. groups, .. relays];
    }
}
