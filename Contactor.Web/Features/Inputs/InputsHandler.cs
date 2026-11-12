using Contactor.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Inputs;

/// <summary>When an input last changed, and to what, from the event log.</summary>
public sealed record InputChange(DateTime TimestampUtc, bool On);

/// <summary>An input as the screens show it.</summary>
public sealed record InputView(int Id, string Name, string ClosedText, string OpenText, bool IsActive, InputChange? LastChange)
{
    /// <summary>The word for a state: the closed text when the board reads the input as on.</summary>
    public string StateText(bool on)
    {
        return on ? ClosedText : OpenText;
    }
}

/// <summary>What the Input screen saves.</summary>
public sealed record InputSettings(int Id, string Name, string ClosedText, string OpenText, bool IsActive);

/// <summary>
/// The board's digital inputs: their names and the words for their states, and when each last changed. Their state
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

    /// <summary>Saves an input's name, state words and "Active". Not logged. Returns null when saved, otherwise the message to show.</summary>
    public async Task<string?> SaveAsync(InputSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var name = settings.Name?.Trim() ?? "";
        var closed = settings.ClosedText?.Trim() ?? "";
        var open = settings.OpenText?.Trim() ?? "";
        if (name.Length == 0)
        {
            return "Enter a name.";
        }

        if (closed.Length == 0 || open.Length == 0)
        {
            return "Enter a word for each state.";
        }

        if (name.Length > Input.TextMaxLength || closed.Length > Input.TextMaxLength || open.Length > Input.TextMaxLength)
        {
            return $"A name or word can be at most {Input.TextMaxLength} characters.";
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var input = settings.Id > _board.Capabilities.InputCount
            ? null
            : await db.Inputs.SingleOrDefaultAsync(i => i.Id == settings.Id, cancellationToken);
        if (input is null)
        {
            return "The input doesn't exist.";
        }

        (input.Name, input.ClosedText, input.OpenText, input.IsActive) = (name, closed, open, settings.IsActive);
        await db.SaveChangesAsync(cancellationToken);
        _changes.Notify();
        return null;
    }

    private static InputView View(Input input, InputChange? lastChange)
    {
        return new InputView(input.Id, input.Name, input.ClosedText, input.OpenText, input.IsActive, lastChange);
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
        return last
            .Where(e => EventText.InputOn(e.Details) is not null)
            .ToDictionary(e => e.InputId, e => new InputChange(e.TimestampUtc, EventText.InputOn(e.Details)!.Value));
    }
}

/// <summary>An input's status line: "Open since 14:02", or just "Open" when it isn't known since when.</summary>
public static class InputStatusText
{
    public static string For(InputView input, bool on, DateTime nowLocal, Func<DateTime, DateTime> toLocal)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(toLocal);
        var state = input.StateText(on);

        // The latest change only tells since when if it went to the state the board reports now; a change made while the
        // app wasn't watching leaves it unknown.
        return input.LastChange is { } change && change.On == on
            ? $"{state} since {RelayStatusText.When(toLocal(change.TimestampUtc), nowLocal)}"
            : state;
    }
}
