using System.Text.Json;
using Contactor.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Persistence;
using Contactor.Web.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Contactor.Web.Features.OwnRules;

/// <summary>
/// What a person does about a board's own rules (issue #497): turn them off, or keep them so the app stops warning, and
/// which rules the app warns about. Only a person's press changes a board's own rules; nothing is ever queued.
/// </summary>
public sealed partial class OwnRulesHandler
{
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly BoardRegistry _registry;
    private readonly OwnRulesService _rules;
    private readonly LocalClock _clock;
    private readonly IStringLocalizer<Texts> _texts;
    private readonly ILogger<OwnRulesHandler> _logger;

    public OwnRulesHandler(
        IDbContextFactory<ContactorDbContext> dbFactory,
        BoardRegistry registry,
        OwnRulesService rules,
        LocalClock clock,
        IStringLocalizer<Texts> texts,
        ILogger<OwnRulesHandler> logger)
    {
        _dbFactory = dbFactory;
        _registry = registry;
        _rules = rules;
        _clock = clock;
        _texts = texts;
        _logger = logger;
    }

    /// <summary>
    /// The board's own rules the app warns about: those the board reports now that no person kept. None while the
    /// board's rules aren't known (it can't read them, or hasn't answered), and for a board the app doesn't run.
    /// </summary>
    public IReadOnlyList<OwnRule> Warnings(int boardId)
    {
        if (_registry.Find(boardId)?.State.OwnRules is not { } rules)
        {
            return [];
        }

        var kept = _rules.Rows(boardId).Where(r => r.KeptUtc is not null).Select(r => new OwnRule(r.Channel, r.Kind)).ToHashSet();
        return [.. rules.Distinct().Where(r => !kept.Contains(r))];
    }

    /// <summary>
    /// Turns off the board's own <paramref name="rules"/> at once, as <paramref name="userEmail"/> pressed, and logs the
    /// rules the board took. Returns null when sent (or none asked), otherwise the message to show: without a
    /// connection, or when the board didn't take them, nothing is logged or queued. The board's next state shows them
    /// gone, and their rows go then.
    /// </summary>
    public async Task<string?> TurnOffAsync(int boardId, IReadOnlyCollection<OwnRule> rules, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        if (_registry.Find(boardId) is not { } board)
        {
            return _texts["Boards.NotChosen"];
        }

        IReadOnlyList<OwnRule> taken;
        try
        {
            taken = await board.TurnOffOwnRulesAsync(rules, cancellationToken);
        }
        catch (BoardNotConnectedException)
        {
            // Also how a board says it took none of them, while it answers.
            return board.State.IsConnected ? _texts["OwnRules.TurnOff.NotTaken"] : _texts["OwnRules.TurnOff.NoContact"];
        }
        catch (BoardRefusedException)
        {
            return _texts["OwnRules.TurnOff.NotTaken"];
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentOutOfRangeException)
        {
            // Rules the board doesn't have, e.g. after it got another model: nothing was sent.
            LogNotSent(ex, boardId);
            return _texts["OwnRules.TurnOff.NotTaken"];
        }

        if (taken.Count == 0)
        {
            return null;
        }

        // The board took them: logged also when the page closed meanwhile.
        await using var db = await _dbFactory.CreateDbContextAsync(CancellationToken.None);
        db.EventLog.Add(new EventLogEntry
        {
            TimestampUtc = _clock.UtcNow,
            BoardId = boardId,
            Actor = userEmail,
            Action = EventActions.BoardOwnRulesTurnedOff,
            Details = JsonSerializer.Serialize(new
            {
                relays = taken.Select(r => r.Relay).Distinct().Order(),
                kinds = taken.Select(r => r.Kind).Distinct().Order().Select(OwnRuleKinds.For),
            }),
        });
        await db.SaveChangesAsync(CancellationToken.None);
        return null;
    }

    /// <summary>
    /// Keeps the board's own rule for the relay on <paramref name="channel"/>, as <paramref name="userEmail"/> chose: the
    /// app no longer warns about it, until the board no longer has it. Logged once. Returns null when kept, otherwise
    /// the message to show.
    /// </summary>
    public async Task<string?> KeepAsync(int boardId, int channel, OwnRuleKind kind, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        return await _rules.KeepAsync(boardId, channel, kind, userEmail, cancellationToken) ? null : _texts["OwnRules.Keep.Gone"].Value;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The own rules of relay board {BoardId} weren't turned off: the board doesn't have them")]
    private partial void LogNotSent(Exception exception, int boardId);
}
