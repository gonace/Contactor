using Contactor.Boards;
using Contactor.Web.Features.Boards;
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
/// <param name="Id">The input, for links and the database.</param>
/// <param name="Channel">Its input on the board: what the board's state goes by, and the number the screens show.</param>
public sealed record InputView(int Id, int Channel, string Name, bool IsActive, InputChange? LastChange)
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
/// itself comes from its board. Inputs above their board's count, left from a board that had more, are left out.
/// </summary>
public sealed class InputsHandler
{
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly BoardRegistry _registry;
    private readonly RelayChanges _changes;

    public InputsHandler(IDbContextFactory<ContactorDbContext> dbFactory, BoardRegistry registry, RelayChanges changes)
    {
        _dbFactory = dbFactory;
        _registry = registry;
        _changes = changes;
    }

    /// <summary>Every input its board has, in order: by board, then channel.</summary>
    public async Task<IReadOnlyList<InputView>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var inputs = (await db.Inputs.AsNoTracking().OrderBy(i => i.BoardId).ThenBy(i => i.Channel).ToListAsync(cancellationToken))
            .Where(i => _registry.HasInput(i.BoardId, i.Channel))
            .ToList();
        var changes = await LastChangesAsync(db, cancellationToken);
        return [.. inputs.Select(i => View(i, changes.GetValueOrDefault(i.Id)))];
    }

    /// <summary>One input, or null for one its board doesn't have.</summary>
    public async Task<InputView?> GetAsync(int inputId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var input = await db.Inputs.AsNoTracking().SingleOrDefaultAsync(i => i.Id == inputId, cancellationToken);
        return input is null || !_registry.HasInput(input.BoardId, input.Channel)
            ? null
            : View(input, (await LastChangesAsync(db, cancellationToken)).GetValueOrDefault(inputId));
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
        var input = await db.Inputs.SingleOrDefaultAsync(i => i.Id == settings.Id, cancellationToken);
        if (input is null || !_registry.HasInput(input.BoardId, input.Channel))
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
        return new InputView(input.Id, input.Channel, input.Name, input.IsActive, lastChange);
    }

    // The latest change of each input, from its newest "input-changed" event; its time is known unless its own board
    // came back after a long gap since.
    private static async Task<Dictionary<int, InputChange>> LastChangesAsync(ContactorDbContext db, CancellationToken cancellationToken)
    {
        var lastIds = db.EventLog
            .Where(e => e.Action == EventActions.InputChanged && e.InputId != null)
            .GroupBy(e => e.InputId)
            .Select(g => g.Max(e => e.Id));
        var last = await db.EventLog.AsNoTracking()
            .Where(e => lastIds.Contains(e.Id))
            .Join(db.Inputs, e => e.InputId, i => i.Id, (e, i) => new { InputId = i.Id, i.BoardId, e.TimestampUtc, e.Details })
            .ToListAsync(cancellationToken);
        var longGapsEnded = await db.BoardRuntimes.AsNoTracking()
            .ToDictionaryAsync(r => r.BoardId, r => r.LongGapEndedUtc, cancellationToken);
        return last
            .Where(e => EventText.InputOn(e.Details) is not null)
            .ToDictionary(
                e => e.InputId,
                e => new InputChange(
                    e.TimestampUtc,
                    EventText.InputOn(e.Details)!.Value,
                    !EventText.InputNoticed(e.Details) && (longGapsEnded.GetValueOrDefault(e.BoardId) is not { } gap || e.TimestampUtc > gap)));
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
