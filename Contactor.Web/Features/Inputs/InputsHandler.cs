using Contactor.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Inputs;

/// <summary>
/// When an input last changed, and to what, from the event log. <paramref name="Known"/> is false when the time can't
/// be trusted: the change was only noticed after a gap, or a long gap came after it.
/// </summary>
public sealed record InputChange(DateTime TimestampUtc, bool On, bool Known = true);

/// <summary>An input as the screens show it.</summary>
public sealed record InputView(int Id, string Name, bool IsActive, InputChange? LastChange)
{
    /// <summary>"Closed" when the board reads the input as on, otherwise "Open".</summary>
    public static string StateText(bool on)
    {
        return Input.StateText(on);
    }
}

/// <summary>What the Input screen saves.</summary>
public sealed record InputSettings(int Id, string Name, bool IsActive);

/// <summary>
/// The board's digital inputs: their names, and when each last changed. Their state
/// itself comes from the board. Inputs above the board's count, left from a board that had more, are left out.
/// </summary>
public sealed class InputsHandler
{
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly IRelayBoard _board;
    private readonly RelayChanges _changes;

    public InputsHandler(IDbContextFactory<ContactorDbContext> dbFactory, IRelayBoard board, RelayChanges changes)
    {
        _dbFactory = dbFactory;
        _board = board;
        _changes = changes;
    }

    /// <summary>Every input on the board, in order.</summary>
    public async Task<IReadOnlyList<InputView>> ListAsync(CancellationToken cancellationToken)
    {
        var count = _board.Capabilities.InputCount;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var inputs = await db.Inputs.AsNoTracking().Where(i => i.Id <= count).OrderBy(i => i.Id).ToListAsync(cancellationToken);
        var changes = await LastChangesAsync(db, cancellationToken);
        return [.. inputs.Select(i => View(i, changes.GetValueOrDefault(i.Id)))];
    }

    /// <summary>One input, or null for a number the board doesn't have.</summary>
    public async Task<InputView?> GetAsync(int inputId, CancellationToken cancellationToken)
    {
        if (inputId < 1 || inputId > _board.Capabilities.InputCount)
        {
            return null;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var input = await db.Inputs.AsNoTracking().SingleOrDefaultAsync(i => i.Id == inputId, cancellationToken);
        return input is null ? null : View(input, (await LastChangesAsync(db, cancellationToken)).GetValueOrDefault(inputId));
    }

    /// <summary>Saves an input's name and "Active". Not logged. Returns null when saved, otherwise the message to show.</summary>
    public async Task<string?> SaveAsync(InputSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var name = settings.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            return "Enter a name.";
        }

        if (name.Length > Input.NameMaxLength)
        {
            return $"The name can be at most {Input.NameMaxLength} characters.";
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var input = settings.Id > _board.Capabilities.InputCount
            ? null
            : await db.Inputs.SingleOrDefaultAsync(i => i.Id == settings.Id, cancellationToken);
        if (input is null)
        {
            return "The input doesn't exist.";
        }

        (input.Name, input.IsActive) = (name, settings.IsActive);
        await db.SaveChangesAsync(cancellationToken);
        _changes.Notify();
        return null;
    }

    private static InputView View(Input input, InputChange? lastChange)
    {
        return new InputView(input.Id, input.Name, input.IsActive, lastChange);
    }

    // The latest change of each input, from its newest "input-changed" event.
    private static async Task<Dictionary<int, InputChange>> LastChangesAsync(ContactorDbContext db, CancellationToken cancellationToken)
    {
        var lastIds = db.EventLog
            .Where(e => e.Action == EventActions.InputChanged && e.InputId != null)
            .GroupBy(e => e.InputId)
            .Select(g => g.Max(e => e.Id));
        var last = await db.EventLog.AsNoTracking()
            .Where(e => lastIds.Contains(e.Id))
            .Select(e => new { InputId = e.InputId!.Value, e.TimestampUtc, e.Details })
            .ToListAsync(cancellationToken);
        var boardId = await db.FirstBoardIdAsync(cancellationToken);
        var longGapEnded = await db.BoardRuntimes.AsNoTracking()
            .Where(r => r.BoardId == boardId)
            .Select(r => r.LongGapEndedUtc)
            .SingleOrDefaultAsync(cancellationToken);
        return last
            .Where(e => EventText.InputOn(e.Details) is not null)
            .ToDictionary(
                e => e.InputId,
                e => new InputChange(
                    e.TimestampUtc,
                    EventText.InputOn(e.Details)!.Value,
                    !EventText.InputNoticed(e.Details) && (longGapEnded is not { } gap || e.TimestampUtc > gap)));
    }
}

/// <summary>The one button on Relays for inactive relays and inputs.</summary>
public static class InactiveText
{
    /// <summary>"Show 2 inactive relays", "Show 1 inactive input", "Show 3 inactive" for both kinds, or "Hide inactive …".</summary>
    public static string For(int relays, int inputs, bool shown)
    {
        var kind = relays > 0 && inputs > 0 ? "" : relays > 0 ? " relays" : " inputs";
        if (shown)
        {
            return $"Hide inactive{kind}";
        }

        var count = relays + inputs;
        return count == 1 ? $"Show 1 inactive{kind.TrimEnd('s')}" : $"Show {count} inactive{kind}";
    }
}

/// <summary>An input's status line, and whether its "since" is unknown.</summary>
public sealed record InputStatus(string Text, bool SinceUnknown);

/// <summary>An input's status line: "Open since 14:02", or "Open since unknown" when the app didn't see the change.</summary>
public static class InputStatusText
{
    /// <summary>Why "since" can be unknown, for the ⓘ next to it.</summary>
    public const string WhyUnknown =
        "The board only tells the app what an input reads now, not since when. The app hasn't seen this input change since it was installed or last had no contact with the board for a while, e.g. after a crash or a power cut.";

    /// <param name="input">The input, with its latest logged change.</param>
    /// <param name="on">What the board reads now: on is closed.</param>
    /// <param name="connected">Whether the app has contact with the board now.</param>
    /// <param name="nowLocal">Now, in local time.</param>
    /// <param name="toLocal">Turns a UTC time into local time.</param>
    public static InputStatus For(InputView input, bool on, bool connected, DateTime nowLocal, Func<DateTime, DateTime> toLocal)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(toLocal);
        var state = InputView.StateText(on);

        // The board reports a change the log doesn't have yet: the page redraws on the board's status a moment before
        // the change is logged. Just the state, until it is. Without contact the state shown may be stale, so not then.
        if (connected && input.LastChange is { } latest && latest.On != on)
        {
            return new InputStatus(state, false);
        }

        // Known for a change to the state the board reports now, which the app saw, with no long gap after it.
        return input.LastChange is { } change && change.On == on && change.Known
            ? new InputStatus($"{state} since {RelayStatusText.When(toLocal(change.TimestampUtc), nowLocal)}", false)
            : new InputStatus($"{state} since unknown", true);
    }
}
