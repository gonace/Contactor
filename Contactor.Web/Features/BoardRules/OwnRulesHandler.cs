using System.Text.Json;
using Contactor.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Languages;
using Contactor.Web.Persistence;
using Contactor.Web.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Contactor.Web.Features.BoardRules;

/// <summary>
/// What a person does about a board's own rules (issue #497): turn them off, or keep them so the app stops warning, and
/// which rules the app warns about. Only a person's press changes a board's own rules; nothing is ever queued.
/// </summary>
public sealed partial class OwnRulesHandler
{
    // An event's details without the properties it has none of.
    private static readonly JsonSerializerOptions LeaveOutNull = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>How long "Turn them off" on the steps of adding a board waits for the board's state to show them gone.</summary>
    public static readonly TimeSpan ConfirmWait = TimeSpan.FromSeconds(5);

    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly BoardRegistry _registry;
    private readonly OwnRulesService _rules;
    private readonly LocalClock _clock;
    private readonly TimeProvider _time;
    private readonly IStringLocalizer<Texts> _texts;
    private readonly ILogger<OwnRulesHandler> _logger;

    public OwnRulesHandler(
        IDbContextFactory<ContactorDbContext> dbFactory,
        BoardRegistry registry,
        OwnRulesService rules,
        LocalClock clock,
        TimeProvider time,
        IStringLocalizer<Texts> texts,
        ILogger<OwnRulesHandler> logger)
    {
        _dbFactory = dbFactory;
        _registry = registry;
        _rules = rules;
        _clock = clock;
        _time = time;
        _texts = texts;
        _logger = logger;
    }

    /// <summary>
    /// Whether a person may keep a rule of this kind on a relay, so the app stops warning; the one place this is decided.
    /// Never one with which the board controls the relay by itself and ignores the app
    /// (<see cref="OwnRuleKind.InputLinkage"/>): the app can't switch that relay, so it stays shown as unavailable until
    /// a person resets it. Never the board's auto-on (<see cref="OwnRuleKind.AutoOn"/>) on a relay in a group
    /// (<paramref name="inGroup"/>): the board would turn it on again without the group's order, so its warning stays
    /// until it is turned off, and a "Keep" from before the relay joined the group is forgotten then
    /// (<see cref="OwnRulesService.ForgetKeepsInGroupsAsync"/>). The same for the board's restart pulse
    /// (<see cref="OwnRuleKind.Restart"/>, issue #515): it would switch the relay without the group's order. Every other
    /// rule may be kept.
    /// </summary>
    public static bool CanKeep(OwnRuleKind kind, bool inGroup)
    {
        return kind switch
        {
            OwnRuleKind.InputLinkage => false,
            OwnRuleKind.AutoOn or OwnRuleKind.Restart => !inGroup,
            _ => true,
        };
    }

    /// <summary>
    /// The kinds a person may keep only on a relay in no group, from <see cref="CanKeep"/> so the two never differ: the
    /// database checks them when a rule is kept (<see cref="OwnRulesService"/>) and when a group is saved
    /// (<see cref="OwnRulesService.ForgetKeepsInGroupsAsync"/>).
    /// </summary>
    public static IReadOnlyList<OwnRuleKind> KeptOnlyOutsideGroups { get; } = [.. Enum.GetValues<OwnRuleKind>().Where(k => CanKeep(k, inGroup: false) && !CanKeep(k, inGroup: true))];

    /// <summary>
    /// Whether the board answers and reads its own rules (<see cref="OwnRulesAbility.Reads"/>) but couldn't read them:
    /// another firmware, a refusal, or a value it can't read. A board that reads them tries to before its first connected
    /// state (<see cref="BoardState.OwnRules"/>), so null rules while it answers mean that. False without the board, before
    /// it answered and while it doesn't answer: its rules are only not known yet. The screens then say to check the
    /// board's own web page (issue #551); nothing is sent to the board.
    /// </summary>
    public static bool CouldntRead(LiveBoard? board)
    {
        return board is { State: { IsConnected: true, OwnRules: null } } && board.Capabilities.OwnRulesOrCantTell.Reads;
    }

    /// <summary>
    /// The relays whose own rules the board answers but couldn't read this time, while it read the others'
    /// (<see cref="OwnRules.Unknown"/>, issue #554), e.g. a VM208 schedule with a time such as 99:00: each screen says so
    /// on that relay, as <see cref="CouldntRead"/> does for the whole board, and never shows it as having none. None
    /// without the board, while it doesn't answer, and while <see cref="CouldntRead"/> holds (the whole board says so).
    /// </summary>
    public static ChannelMask CouldntReadRelays(LiveBoard? board)
    {
        return board is { State: { IsConnected: true, OwnRules: { } rules } } && board.Capabilities.OwnRulesOrCantTell.Reads
            ? rules.Unknown
            : ChannelMask.None;
    }

    /// <summary>
    /// Which of <paramref name="channels"/> (e.g. a group's relays) are among the board's relays whose rules it couldn't
    /// read (<see cref="CouldntReadRelays"/>), in the order given: a group's card and page name them, since its relays
    /// have no card of their own.
    /// </summary>
    public IReadOnlyList<int> CouldntRead(int boardId, IEnumerable<int> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var unknown = CouldntReadRelays(_registry.Find(boardId));
        return unknown.IsEmpty ? [] : [.. channels.Where(unknown.Contains)];
    }

    /// <summary>
    /// The board's own rules the app warns about: those the board reports now that no person kept. None while the
    /// board's rules aren't known (it can't read them, or hasn't answered), until the app has read which rules people
    /// kept (just after the start), and for a board the app doesn't run.
    /// </summary>
    public IReadOnlyList<OwnRule> Warnings(int boardId)
    {
        if (_registry.Find(boardId)?.State.OwnRules is not { } rules || !_rules.TryGetRows(boardId, out var rows))
        {
            return [];
        }

        var kept = rows.Where(r => r.KeptUtc is not null).Select(r => new OwnRule(r.Channel, r.Kind)).ToHashSet();
        return [.. rules.Distinct().Where(r => !kept.Contains(r))];
    }

    /// <summary>
    /// The board's own rules to ask about while it is being added ("Add a board" and the setup guide): those it reports
    /// now that no person kept, in relay order, for a board that reads them; none for a board that can't (it has none,
    /// or can't tell) or hasn't reported them yet. Unlike <see cref="Warnings"/>, also before the app has read which
    /// rules people kept: a board just added has none kept.
    /// </summary>
    public IReadOnlyList<OwnRule> ToAsk(int boardId)
    {
        if (_registry.Find(boardId) is not { } board || !board.Capabilities.OwnRulesOrCantTell.Reads || board.State.OwnRules is not { } rules)
        {
            return [];
        }

        var kept = _rules.Rows(boardId).Where(r => r.KeptUtc is not null).Select(r => new OwnRule(r.Channel, r.Kind)).ToHashSet();
        return [.. rules.Distinct().Where(r => !kept.Contains(r)).OrderBy(r => r.Relay).ThenBy(r => r.Kind)];
    }

    /// <summary>
    /// Whether the app can turn off <paramref name="rule"/> now: the board turns its rules off
    /// (<see cref="OwnRulesAbility.TurnsOff"/>) and its state doesn't name the rule among those it can't
    /// (<see cref="OwnRules.CantTurnOff"/>, e.g. a timer started on the VM208's own page, which ends by itself). While the
    /// board's rules aren't known, as the ability says. Never a rule on a relay whose rules the board couldn't read
    /// (<see cref="OwnRules.Unknown"/>): it doesn't know what is there to turn off. A screen offers "Turn off" only for
    /// such a rule.
    /// </summary>
    public bool CanTurnOff(int boardId, OwnRule rule)
    {
        return _registry.Find(boardId) is { } board
            && board.Capabilities.OwnRulesOrCantTell.TurnsOff
            && board.State.OwnRules?.CanTurnOff(rule) != false
            && board.State.OwnRules?.Unknown.Contains(rule.Relay) != true;
    }

    /// <summary>
    /// Whether the "Running on the board right now" rows offer a "Keep" for after the run, and say when it can be pressed:
    /// only on a board that turns its rules off (<see cref="OwnRulesAbility.TurnsOff"/>), where the relay may also have a
    /// schedule on the board to turn off or keep then (the VM208). A board that can't (the VM204, whose only rules are its
    /// pulses) has nothing left on the relay once the run ends, so the rows show neither.
    /// </summary>
    public bool OffersKeepAfterRun(int boardId)
    {
        return _registry.Find(boardId)?.Capabilities.OwnRulesOrCantTell.TurnsOff == true;
    }

    /// <summary>
    /// Whether <paramref name="rule"/> runs on the board right now and ends by itself: the board's state names it among
    /// the rules the app can't turn off (<see cref="OwnRules.CantTurnOff"/>), e.g. a pulse or a timer started on the
    /// VM208's own page. The screens show it in a group of its own, "Running on the board right now", with no "Turn off"
    /// and a "Keep" that can't be pressed; <see cref="KeepAsync"/> refuses it until it has ended (issue #552).
    /// </summary>
    public bool RunsOnBoard(int boardId, OwnRule rule)
    {
        return _registry.Find(boardId)?.State.OwnRules?.CantTurnOff.Contains(rule) == true;
    }

    /// <summary>The rules of <paramref name="rules"/> the app can turn off now (<see cref="CanTurnOff(int, OwnRule)"/>), in the order given.</summary>
    public IReadOnlyList<OwnRule> TurnsOff(int boardId, IEnumerable<OwnRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return [.. rules.Where(r => CanTurnOff(boardId, r))];
    }

    /// <summary>
    /// The other rules of the board's own that turning off <paramref name="rules"/> also removes on the board
    /// (<see cref="OwnRules.GoesWith(IEnumerable{OwnRule})"/>), e.g. relay 2's timer when relay 1's timer is a schedule
    /// on the board that switches both: each once, in the board's order. None for a board whose rules go alone, or
    /// while its rules aren't known. A screen names them before a person presses "Turn off".
    /// </summary>
    public IReadOnlyList<OwnRule> AlsoTurnsOff(int boardId, IReadOnlyCollection<OwnRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return _registry.Find(boardId)?.State.OwnRules?.GoesWith(rules) ?? [];
    }

    /// <summary>
    /// "Turn them off" while the board is being added: turns them off as <see cref="TurnOffAsync"/> does (logged when the
    /// board took them, never queued), then waits up to <see cref="ConfirmWait"/> for the board's state to no longer have
    /// any of them, asking it for a fresh state once. Returns null once confirmed, otherwise the message to show.
    /// </summary>
    public async Task<string?> TurnOffAndConfirmAsync(int boardId, IReadOnlyCollection<OwnRule> rules, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, BoardStateChange change)
        {
            if (change.BoardId == boardId && AreGone(change.State, rules))
            {
                gone.TrySetResult();
            }
        }

        // Listening from before the command: the board's answer may come before it returns.
        _registry.StateChanged += OnChanged;
        _registry.OwnRulesChanged += OnChanged;
        try
        {
            if (await TurnOffAsync(boardId, rules, userEmail, cancellationToken) is { } error)
            {
                return error;
            }

            if (_registry.Find(boardId) is not { } board)
            {
                return _texts["Boards.NotChosen"];
            }

            if (AreGone(board.State, rules))
            {
                return null;
            }

            try
            {
                // A board that says nothing of its own on such a change answers this with its state.
                await board.RefreshAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is BoardNotConnectedException or BoardRefusedException)
            {
                // No fresh state: what the board sends on its own still counts, until the wait is over.
            }

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var wait = Task.Delay(ConfirmWait, _time, stop.Token);
            await Task.WhenAny(gone.Task, wait);
            await stop.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return gone.Task.IsCompleted || AreGone(board.State, rules)
                ? null
                : _texts["OwnRules.Step.NotConfirmed" + Plurals.Form(rules.Distinct().Count())].Value;
        }
        finally
        {
            _registry.StateChanged -= OnChanged;
            _registry.OwnRulesChanged -= OnChanged;
        }
    }

    /// <summary>
    /// "Keep them" while the board is being added: keeps each of <paramref name="rules"/> a person may keep
    /// (<see cref="CanKeep"/>, on the board's relays as they are in groups now, and none that runs on the board right now,
    /// <see cref="RunsOnBoard"/>), as <see cref="KeepAsync"/> does, at once: a rule the app hasn't noted yet is noted now
    /// (<see cref="OwnRulesService.KeepReportedAsync"/>). The others are left as they are, nothing saved for them.
    /// Returns null when all are kept (or none could be), otherwise the message to show; then none is kept.
    /// </summary>
    public async Task<string?> KeepAllAsync(int boardId, IReadOnlyCollection<OwnRule> rules, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        Dictionary<int, string> grouped;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            grouped = await OwnRulesService.GroupedRelaysAsync(db, boardId, cancellationToken);
        }

        // Never one that runs on the board right now: it can be kept once it has ended (RunsOnBoard).
        List<OwnRule> keepable = [.. rules.Where(r => CanKeep(r.Kind, grouped.ContainsKey(r.Relay)) && !RunsOnBoard(boardId, r))];
        if (keepable.Count == 0)
        {
            return null;
        }

        return await WordsAsync(boardId, await _rules.KeepReportedAsync(boardId, keepable, userEmail, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Turns off the board's own <paramref name="rules"/> at once, as <paramref name="userEmail"/> pressed, and logs
    /// what went off: the rules the board took, the rules that went with them ("also"), and the rules of which only part
    /// went off ("partly", issue #604), as the board reported them (<see cref="OwnRulesTurnOff"/>). Returns null when
    /// sent (or none asked), otherwise the message to show: without a connection, or when nothing went off, nothing is
    /// logged or queued. When the board took none of them but part of them went off, that is logged and the message
    /// says they weren't turned off. The board's next state shows what is gone, and their rows go then.
    /// </summary>
    public async Task<string?> TurnOffAsync(int boardId, IReadOnlyCollection<OwnRule> rules, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        if (_registry.Find(boardId) is not { } board)
        {
            return _texts["Boards.NotChosen"];
        }

        OwnRulesTurnOff result;
        try
        {
            result = await board.TurnOffOwnRulesAsync(rules, cancellationToken);
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

        if (!result.AnyOff)
        {
            return null;
        }

        // What went off: logged also when the page closed meanwhile, with what went with it (none: no "also" or "partly").
        var taken = result.Taken;
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
                also = Rules(result.AlsoOff),
                partly = Rules(result.PartlyOff),
            }, LeaveOutNull),
        });
        await db.SaveChangesAsync(CancellationToken.None);

        // Part of them went off, but the board took none of those asked: the person is told, as when nothing went off.
        return taken.Count == 0 ? _texts["OwnRules.TurnOff.NotTaken"].Value : null;

        // Each rule's relay and kind, in relay order; null for none, so the details leave it out.
        static object? Rules(IReadOnlyList<OwnRule> list)
        {
            return list.Count == 0 ? null : list.OrderBy(r => r.Relay).ThenBy(r => r.Kind).Select(r => new { relay = r.Relay, kind = OwnRuleKinds.For(r.Kind) }).ToList();
        }
    }

    /// <summary>
    /// Keeps the board's own rule for the relay on <paramref name="channel"/>, as <paramref name="userEmail"/> chose: the
    /// app no longer warns about it, until the board no longer has it. Logged once. Returns null when kept, otherwise
    /// the message to show, also for a rule a person may not keep (<see cref="CanKeep"/>, e.g. the auto-on of a relay in
    /// a group), and for one that runs on the board right now (<see cref="RunsOnBoard"/>), until it has ended.
    /// </summary>
    public async Task<string?> KeepAsync(int boardId, int channel, OwnRuleKind kind, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        if (!CanKeep(kind, inGroup: false))
        {
            // No "Keep" is offered for it; nothing is saved.
            return _texts["OwnRules.Keep.Unavailable"].Value;
        }

        if (RunsOnBoard(boardId, new OwnRule(channel, kind)))
        {
            // Its "Keep" can't be pressed while it runs; a page drawn before the run started gets this. Nothing is saved.
            return _texts["OwnRules.Running.Later"].Value;
        }

        // The service refuses the auto-on of a relay in a group in the same write that keeps it (no "Keep" is offered
        // for it either), so a group saved at the same moment is never crossed.
        return await WordsAsync(boardId, await _rules.KeepAsync(boardId, channel, kind, userEmail, cancellationToken), cancellationToken);
    }

    // A keep's outcome in words: null when kept, "Can't be kept: Heat is in a group, …" when the relay is in a group,
    // otherwise "The board no longer has that rule.".
    private async Task<string?> WordsAsync(int boardId, KeepOutcome outcome, CancellationToken cancellationToken)
    {
        if (outcome.Kept)
        {
            return null;
        }

        if (outcome.InGroup is not { } channel)
        {
            return _texts["OwnRules.Keep.Gone"].Value;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var name = await db.Relays.Where(r => r.BoardId == boardId && r.Channel == channel).Select(r => r.Name).SingleOrDefaultAsync(cancellationToken);
        return InGroup(channel, name);
    }

    /// <summary>
    /// Why a rule on a relay in a group can't be kept (<see cref="CanKeep"/>), shown where "Keep" would be: "Can't be
    /// kept: Heat is in a group, and the board would turn it on again without the group's order." The relay by its name,
    /// or "Relay 3" without one.
    /// </summary>
    public string InGroup(int channel, string? name)
    {
        return _texts["OwnRules.Keep.InGroup", string.IsNullOrWhiteSpace(name) ? _texts["Relays.DefaultName", channel].Value : name];
    }

    // Whether the board's state shows none of the rules any more: a state that knows its rules, without them. A rule on a
    // relay whose rules it couldn't read (OwnRules.Unknown) isn't known to be gone.
    private static bool AreGone(BoardState state, IReadOnlyCollection<OwnRule> rules)
    {
        return state.OwnRules is { } now && !rules.Any(r => now.Contains(r) || now.Unknown.Contains(r.Relay));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The own rules of relay board {BoardId} weren't turned off: the board doesn't have them")]
    private partial void LogNotSent(Exception exception, int boardId);
}
