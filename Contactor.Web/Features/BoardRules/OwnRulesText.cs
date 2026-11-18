using System.Globalization;
using Contactor.Boards;
using Contactor.Web.Features.Languages;
using Contactor.Web.Features.Relays;
using Contactor.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Contactor.Web.Features.BoardRules;

/// <summary>
/// The words about a board's own rules on Relays, the board's page and Settings (issue #497). Each text is keyed by
/// the rules' kind ("Timer"), with "Rule" for any other kind or a mix, so a board with other kinds only adds keys.
/// </summary>
public sealed class OwnRulesText
{
    private readonly IStringLocalizer<Texts> _texts;
    private readonly BoardsText _boards;

    public OwnRulesText(IStringLocalizer<Texts> texts, BoardsText boards)
    {
        _texts = texts;
        _boards = boards;
    }

    /// <summary>
    /// The warning's heading: "The board has timers of its own", or with two or more boards "Garage has timers of its
    /// own" (<paramref name="boardName"/>).
    /// </summary>
    public string Heading(IReadOnlyCollection<OwnRule> rules, string? boardName)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return _texts[$"OwnRules.Warning.{Kind(rules.Select(r => r.Kind))}{Plurals.Form(rules.Count)}", boardName ?? _texts["OwnRules.TheBoard"]];
    }

    /// <summary>The warning's text: "They can switch relays 3 and 5 without Contactor."</summary>
    public string CanSwitch(IReadOnlyCollection<OwnRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return _texts["OwnRules.CanSwitch" + Plurals.Form(rules.Count), Relays(rules.Select(r => r.Relay))];
    }

    /// <summary>The warning's button: "Turn them off", or "Turn it off" for one rule.</summary>
    public string TurnOffAll(int count)
    {
        return _texts["OwnRules.TurnOffAll" + Plurals.Form(count)];
    }

    /// <summary>The step's heading while the board is being added: "This board has timers of its own".</summary>
    public string StepTitle(IReadOnlyCollection<OwnRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return _texts[$"OwnRules.Step.Title.{Kind(rules.Select(r => r.Kind))}{Plurals.Form(rules.Count)}"];
    }

    /// <summary>The step's text under its heading: "They can turn relays on and off without Contactor."</summary>
    public string StepIntro(int count)
    {
        return _texts["OwnRules.Step.Intro" + Plurals.Form(count)];
    }

    /// <summary>The step's advice: "We recommend turning them off."</summary>
    public string Recommend(int count)
    {
        return _texts["OwnRules.Step.Recommend" + Plurals.Form(count)];
    }

    /// <summary>The step's second button: "Keep them", or "Keep it" for one rule.</summary>
    public string KeepAll(int count)
    {
        return _texts["OwnRules.KeepAll" + Plurals.Form(count)];
    }

    /// <summary>The line on a relay's card: "The board's timer can switch it".</summary>
    public string CardLine(IEnumerable<OwnRuleKind> kinds)
    {
        return _texts[$"OwnRules.CardLine.{Kind(kinds)}"];
    }

    /// <summary>
    /// The line on a group's card, naming its relays by their role in the group: "The board's timer can switch Heat",
    /// "… Fan and Heat". Each name once, in the order given.
    /// </summary>
    public string GroupLine(IEnumerable<OwnRuleKind> kinds, IEnumerable<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return _texts[$"OwnRules.GroupLine.{Kind(kinds)}", _boards.And([.. roles.Distinct()])];
    }

    /// <summary>The board's card's heading: "The board's own timers".</summary>
    public string CardTitle(IEnumerable<OwnRuleKind> kinds)
    {
        return _texts[$"OwnRules.Card.Title.{Kind(kinds)}"];
    }

    /// <summary>
    /// "Found today at 07:12", "Found yesterday at 07:12", "Found 3 Nov at 07:12": the time always 24 hours with ":",
    /// the date with the language's month names, as the relays' status lines write them.
    /// </summary>
    public string Found(DateTime local, DateTime nowLocal)
    {
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return (local.Date - nowLocal.Date).Days switch
        {
            0 => _texts["OwnRules.Found.Today", time],
            -1 => _texts["OwnRules.Found.Yesterday", time],
            _ => _texts["OwnRules.Found.Date", local.ToString(_texts["Relays.When.DateFormat"], CultureInfo.CurrentCulture), time],
        };
    }

    /// <summary>"relay 3", "relays 3 and 5", "relays 1, 3 and 5": each relay once, in order.</summary>
    public string Relays(IEnumerable<int> relays)
    {
        List<string> numbers = [.. relays.Distinct().Order().Select(r => r.ToString(CultureInfo.CurrentCulture))];
        return _texts[numbers.Count == 1 ? "OwnRules.Relay" : "OwnRules.Relays", _boards.And(numbers)];
    }

    // The key's kind: "Timer" when every rule is a timer (or none is known), otherwise "Rule".
    private static string Kind(IEnumerable<OwnRuleKind> kinds)
    {
        return kinds.All(k => k == OwnRuleKind.Timer) ? "Timer" : "Rule";
    }
}
