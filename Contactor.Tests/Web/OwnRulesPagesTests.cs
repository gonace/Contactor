using System.Net;
using System.Text.RegularExpressions;
using Contactor.Boards;
using Contactor.Boards.Velleman;
using Contactor.Tests.Boards;
using Contactor.Web.Components;
using Contactor.Web.Features.BoardRules;
using Contactor.Web.Features.Groups;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// A board's own rules on the screens (issue #497): the red warning on Relays with "Turn them off" and a line on each
/// relay they can switch, and the card on the board's page and on Settings with one board.
/// </summary>
public sealed partial class OwnRulesPagesTests
{
    private const string John = "john@example.com";
    private const int BoardId = 1;

    private static readonly OwnRule Timer3 = new(3, OwnRuleKind.Timer);
    private static readonly OwnRule Timer5 = new(5, OwnRuleKind.Timer);

    // What follows the warning: the cards with one board, a board section's headings with several.
    private static readonly string[] BoxEnds = ["<main", "<h3"];

    // 07:12 in the app's time zone (Europe/Stockholm).
    private static readonly DateTimeOffset NowUtc = new(2026, 11, 17, 6, 12, 0, TimeSpan.Zero);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_ListTheRelays_InWords()
    {
        Assert.Equal("relay 3", TestTexts.OwnRules.Relays([3]));
        Assert.Equal("relays 3 and 5", TestTexts.OwnRules.Relays([5, 3, 5]));
        Assert.Equal("relays 1, 3 and 5", TestTexts.OwnRules.Relays([3, 1, 5]));
        Assert.Equal("The board has a timer of its own", TestTexts.OwnRules.Heading([Timer3], null));
        Assert.Equal("Garage has timers of its own", TestTexts.OwnRules.Heading([Timer3, Timer5], "Garage"));
        Assert.Equal("The board has rules of its own", TestTexts.OwnRules.Heading([Timer3, new OwnRule(5, OwnRuleKind.InputEdge)], null));
        Assert.Equal("It can switch relay 3 without Contactor.", TestTexts.OwnRules.CanSwitch([Timer3]));
        Assert.Equal(("Turn it off", "Turn them off"), (TestTexts.OwnRules.TurnOffAll(1), TestTexts.OwnRules.TurnOffAll(2)));
        Assert.Equal("Found today at 07:12", TestTexts.OwnRules.Found(new DateTime(2026, 11, 17, 7, 12, 0), new DateTime(2026, 11, 17, 23, 59, 0)));
        Assert.Equal("Found yesterday at 07:12", TestTexts.OwnRules.Found(new DateTime(2026, 11, 16, 7, 12, 0), new DateTime(2026, 11, 17, 0, 1, 0)));
        Assert.Equal("Found 3 Nov at 07:12", TestTexts.OwnRules.Found(new DateTime(2026, 11, 3, 7, 12, 0), new DateTime(2026, 11, 17, 9, 0, 0)));
        Assert.Equal("The board's timer can switch Heat", TestTexts.OwnRules.GroupLine([OwnRuleKind.Timer], ["Heat"]));
        Assert.Equal("The board's timer can switch Fan and Heat", TestTexts.OwnRules.GroupLine([OwnRuleKind.Timer, OwnRuleKind.Timer], ["Fan", "Heat", "Heat"]));
    }

    [Fact]
    public void Should_ListTheRelays_InSwedish()
    {
        using var swedish = TestCulture.Language("sv");

        Assert.Equal("relä 1, 3 och 5", TestTexts.OwnRules.Relays([3, 1, 5]));
        Assert.Equal("Kortet har egna timers", TestTexts.OwnRules.Heading([Timer3, Timer5], null));
        Assert.Equal("Garage har egna timers", TestTexts.OwnRules.Heading([Timer3, Timer5], "Garage"));
        Assert.Equal("De kan slå om relä 3 och 5 utan Contactor.", TestTexts.OwnRules.CanSwitch([Timer3, Timer5]));
        Assert.Equal("Stäng av dem", TestTexts.OwnRules.TurnOffAll(2));
        Assert.Equal("Kortets timer kan slå om det", TestTexts.OwnRules.CardLine([OwnRuleKind.Timer]));
        Assert.Equal("Kortets egna timers", TestTexts.OwnRules.CardTitle([OwnRuleKind.Timer]));
        Assert.Equal("Hittades i dag 07:12", TestTexts.OwnRules.Found(new DateTime(2026, 11, 17, 7, 12, 0), new DateTime(2026, 11, 17, 9, 0, 0)));
        Assert.Equal("Hittades i går 07:12", TestTexts.OwnRules.Found(new DateTime(2026, 11, 16, 7, 12, 0), new DateTime(2026, 11, 17, 9, 0, 0)));
        Assert.Equal("Kortets timer kan slå om Värme", TestTexts.OwnRules.GroupLine([OwnRuleKind.Timer], ["Värme"]));
        Assert.Equal("Kortets timer kan slå om Fläkt och Värme", TestTexts.OwnRules.GroupLine([OwnRuleKind.Timer], ["Fläkt", "Värme"]));
    }

    [Fact]
    public async Task Should_WarnOnRelays_AndOnEachRelayTheTimersCanSwitch()
    {
        var board = Board();
        await using var app = App(board);
        await RulesAsync(app, board, Timer3, Timer5);

        var html = await PageAsync(app, "/");

        var box = Box(html);
        Assert.Contains("The board has timers of its own", box, StringComparison.Ordinal);
        Assert.Contains("They can switch relays 3 and 5 without Contactor.", box, StringComparison.Ordinal);
        Assert.Contains(">Turn them off</button>", box, StringComparison.Ordinal);

        // Under the connection line, before the cards.
        Assert.True(html.IndexOf("class=\"online\"", StringComparison.Ordinal) < html.IndexOf("own-rules", StringComparison.Ordinal));
        Assert.True(html.IndexOf("own-rules", StringComparison.Ordinal) < html.IndexOf("href=\"relay/1\"", StringComparison.Ordinal));
        Assert.Contains("The board's timer can switch it", CardText(html, "relay/3"), StringComparison.Ordinal);
        Assert.Contains("The board's timer can switch it", CardText(html, "relay/5"), StringComparison.Ordinal);
        Assert.DoesNotContain("The board's timer", CardText(html, "relay/4"), StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(html, "own-rule-line"));

        // A board whose rules go alone says nothing more.
        Assert.DoesNotContain("Also", box, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_WordTheAutoOn_AndWhatGoesWithATurnOff()
    {
        var autoOn3 = new OwnRule(3, OwnRuleKind.AutoOn);
        Dictionary<int, string> names = new() { [5] = "Heat", [6] = "Fan" };
        Assert.Equal("The board turns a relay on again by itself", TestTexts.OwnRules.Heading([autoOn3], null));
        Assert.Equal("Garage turns relays on again by itself", TestTexts.OwnRules.Heading([autoOn3, new OwnRule(5, OwnRuleKind.AutoOn)], "Garage"));
        Assert.Equal("The board has rules of its own", TestTexts.OwnRules.Heading([autoOn3, Timer5], null));
        Assert.Equal("The board turns it on again by itself after it goes off", TestTexts.OwnRules.CardLine([OwnRuleKind.AutoOn]));
        Assert.Equal("The board turns Heat on again by itself", TestTexts.OwnRules.GroupLine([OwnRuleKind.AutoOn], ["Heat"]));
        Assert.Equal("Relays the board turns on again", TestTexts.OwnRules.CardTitle([OwnRuleKind.AutoOn]));
        Assert.Equal("This board turns a relay on again by itself", TestTexts.OwnRules.StepTitle([autoOn3]));
        Assert.Null(TestTexts.OwnRules.AlsoTurnsOff([], names));
        Assert.Equal("Also turns off the board's timer for Heat.", TestTexts.OwnRules.AlsoTurnsOff([Timer5], names));
        Assert.Equal("Also turns off the board's timer for Relay 3 and Heat.", TestTexts.OwnRules.AlsoTurnsOff([Timer5, Timer3], names));
        Assert.Equal("Also turns off the board's timer for Relay 3.", TestTexts.OwnRules.AlsoTurnsOff([Timer3], null));
        Assert.Equal("Also stops the board turning Fan on again by itself.", TestTexts.OwnRules.AlsoTurnsOff([new OwnRule(6, OwnRuleKind.AutoOn)], names));
        Assert.Equal("Also turns off the board's own rules for Relay 3 and Heat.", TestTexts.OwnRules.AlsoTurnsOff([autoOn3, Timer5], names));
        Assert.Equal("Also stops the inputs switching Heat.", TestTexts.OwnRules.AlsoTurnsOff([new OwnRule(5, OwnRuleKind.InputEdge), new OwnRule(5, OwnRuleKind.InputToggle)], names));
        Assert.Equal("Also resets Heat.", TestTexts.OwnRules.AlsoTurnsOff([new OwnRule(5, OwnRuleKind.InputLinkage)], names));
        Assert.Equal("Also stops the inputs switching Heat.", TestTexts.OwnRules.AlsoTurnsOff([new OwnRule(5, OwnRuleKind.InputLinkage), new OwnRule(5, OwnRuleKind.InputToggle)], names));
        Assert.Equal("Also stops the inputs switching Fan.", TestTexts.OwnRules.AlsoTurnsOff([new OwnRule(6, OwnRuleKind.InputToggle)], names));

        // Two relays with the same name are both named.
        Assert.Equal("Also turns off the board's timer for Heat and Heat.", TestTexts.OwnRules.AlsoTurnsOff([Timer3, Timer5], new Dictionary<int, string> { [3] = "Heat", [5] = "Heat" }));

        using var swedish = TestCulture.Language("sv");
        names = new() { [5] = "Värme", [6] = "Fläkt" };
        Assert.Equal("Kortet slår på ett relä igen av sig självt", TestTexts.OwnRules.Heading([autoOn3], null));
        Assert.Equal("Garage slår på reläer igen av sig självt", TestTexts.OwnRules.Heading([autoOn3, new OwnRule(5, OwnRuleKind.AutoOn)], "Garage"));
        Assert.Equal("Kortet slår på det igen av sig självt när det har stängts av", TestTexts.OwnRules.CardLine([OwnRuleKind.AutoOn]));
        Assert.Equal("Kortet slår på Värme igen av sig självt", TestTexts.OwnRules.GroupLine([OwnRuleKind.AutoOn], ["Värme"]));
        Assert.Equal("Reläer som kortet slår på igen", TestTexts.OwnRules.CardTitle([OwnRuleKind.AutoOn]));
        Assert.Equal("Kortet slår på reläer igen av sig självt", TestTexts.OwnRules.StepTitle([autoOn3, new OwnRule(5, OwnRuleKind.AutoOn)]));
        Assert.Equal("Stänger också av kortets timer för Värme.", TestTexts.OwnRules.AlsoTurnsOff([Timer5], names));
        Assert.Equal("Stänger också av kortets timer för Relä 3 och Värme.", TestTexts.OwnRules.AlsoTurnsOff([Timer3, Timer5], names));
        Assert.Equal("Gör också att kortet inte längre slår på Fläkt igen av sig självt.", TestTexts.OwnRules.AlsoTurnsOff([new OwnRule(6, OwnRuleKind.AutoOn)], names));
        Assert.Equal("Stänger också av kortets egna regler för Relä 3 och Värme.", TestTexts.OwnRules.AlsoTurnsOff([autoOn3, Timer5], names));
    }

    // Relay 3's timer is a schedule on the board that also switches relay 5 (Heat), which a person kept: the warning
    // says before the press that "Turn it off" takes Heat's too, and it does.
    [Fact]
    public async Task Should_SayOnRelays_WhatGoesWithTheTurnOff_AndTurnItOff()
    {
        var board = Board();
        await using var app = App(board);
        await NameAsync(app, 5, "Heat");
        await RulesAsync(app, board, Linked());
        Assert.Null(await app.Services.GetRequiredService<OwnRulesHandler>().KeepAsync(BoardId, 5, OwnRuleKind.Timer, John, Cancel));

        var box = Box(await PageAsync(app, "/"));
        Assert.Contains("It can switch relay 3 without Contactor.", box, StringComparison.Ordinal);
        Assert.Contains("Also turns off the board's timer for Heat.", box, StringComparison.Ordinal);
        Assert.True(box.IndexOf("Also turns off", StringComparison.Ordinal) < box.IndexOf(">Turn it off</button>", StringComparison.Ordinal));

        using var page = await PageRenderer.StartAsync<Relays>(app.Services, John, "John");
        await page.ClickAsync(PageRenderer.WithText("Turn it off"));

        Assert.Equal(OwnRules.Empty, board.State.OwnRules);
        await Wait.UntilAsync(() => Service(app).Rows(BoardId).Count == 0);
        var entry = (await EventsAsync(app)).Single(e => e.Action == EventActions.BoardOwnRulesTurnedOff);
        Assert.Contains("\"relays\":[3]", entry.Details, StringComparison.Ordinal);
        Assert.Contains("\"also\":[{\"relay\":5,\"kind\":\"timer\"}]", entry.Details, StringComparison.Ordinal);
        Assert.Empty(page.Errors);
    }

    // Only a board that turns its rules off says what goes with them.
    [Fact]
    public async Task Should_SayNothingMore_ForABoardThatCantTurnItsRulesOff()
    {
        var board = Board(new OwnRulesAbility(Reads: true, TurnsOff: false, MayHaveUnseen: false));
        await using var app = App(board);
        await RulesAsync(app, board, Linked());

        Assert.DoesNotContain("Also", Box(await PageAsync(app, "/")), StringComparison.Ordinal);
        Assert.DoesNotContain("Also", RulesCard(await PageAsync(app, $"/board/{BoardId}")), StringComparison.Ordinal);
    }

    // On the board's page the row of the rule that takes another says so under it, by the relay's name; pressing its
    // "Turn off" removes both, the event names the one asked, and both rows go with the board's next state.
    [Fact]
    public async Task Should_SayOnTheBoardsPage_WhatGoesWithARulesTurnOff()
    {
        var board = Board();
        await using var app = App(board);
        await NameAsync(app, 5, "Heat");
        await RulesAsync(app, board, Linked());

        var card = RulesCard(await PageAsync(app, $"/board/{BoardId}"));
        var rows = Regex.Split(card, "<li[^>]*>").Skip(1).Select(r => WebUtility.HtmlDecode(Tags().Replace(r, " "))).Select(r => Regex.Replace(r, "\\s+", " ").Trim()).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Contains("Also turns off the board's timer for Heat.", rows[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Also", rows[1], StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"own-rule-1-3-timer\"", card, StringComparison.Ordinal);

        using var page = await PageRenderer.StartAsync<BoardPage>(app.Services, John, "John", new Dictionary<string, object?> { [nameof(BoardPage.Id)] = BoardId });
        await Wait.UntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains("Also turns off", StringComparison.Ordinal));
        await page.ClickAsync(PageRenderer.WithText("Turn off"));

        Assert.Equal(OwnRules.Empty, board.State.OwnRules);
        await Wait.UntilAsync(() => Service(app).Rows(BoardId).Count == 0);
        var entry = (await EventsAsync(app)).Single();
        Assert.Equal(EventActions.BoardOwnRulesTurnedOff, entry.Action);
        Assert.Contains("\"relays\":[3]", entry.Details, StringComparison.Ordinal);
        Assert.Contains("\"also\":[{\"relay\":5,\"kind\":\"timer\"}]", entry.Details, StringComparison.Ordinal);
        Assert.Empty(page.Errors);
    }

    // The auto-on of a relay in a group (Heat, relay 2) can't be kept: no "Keep" on the card, a line why in its place, and
    // the handler refuses it. An auto-on on a relay in no group (relay 5) and a timer keep their "Keep", also in "Keep them".
    [Fact]
    public async Task Should_RefuseToKeepAnAutoOn_OnARelayInAGroup()
    {
        var auto2 = new OwnRule(2, OwnRuleKind.AutoOn);
        var auto5 = new OwnRule(5, OwnRuleKind.AutoOn);
        var board = Board();
        await using var app = App(board);
        await NameAsync(app, 2, "Heat");
        Assert.Null((await CreateGroupAsync(app)).Error);
        await RulesAsync(app, board, auto2, Timer3, auto5);
        const string Why = "Can't be kept: Heat is in a group, and the board would turn it on again without the group's order.";

        var card = RulesCard(await PageAsync(app, $"/board/{BoardId}"));
        var rows = Regex.Split(card, "<li[^>]*>").Skip(1).Select(r => WebUtility.HtmlDecode(Tags().Replace(r, " "))).Select(r => Regex.Replace(r, "\\s+", " ").Trim()).ToList();
        Assert.Equal(3, rows.Count);
        Assert.EndsWith("Turn off " + Why, rows[0], StringComparison.Ordinal);
        Assert.EndsWith("Turn off Keep", rows[1], StringComparison.Ordinal);
        Assert.EndsWith("Turn off Keep", rows[2], StringComparison.Ordinal);

        var handler = app.Services.GetRequiredService<OwnRulesHandler>();
        Assert.Equal(Why, await handler.KeepAsync(BoardId, 2, OwnRuleKind.AutoOn, John, Cancel));
        Assert.Null(await handler.KeepAllAsync(BoardId, [auto2, Timer3, auto5], John, Cancel));
        Assert.Equal([auto2], handler.Warnings(BoardId));
        Assert.Contains("The board turns a relay on again by itself", Box(await PageAsync(app, "/")), StringComparison.Ordinal);
        Assert.Equal(2, (await EventsAsync(app)).Count(e => e.Action == EventActions.BoardOwnRuleKept));

        using var swedish = TestCulture.Language("sv");
        Assert.Equal("Kan inte behållas: Värme ingår i en grupp, och kortet skulle slå på det igen utan gruppens ordning.", TestTexts.Localizer["OwnRules.Keep.InGroup", "Värme"].Value);
    }

    // A kept auto-on whose relay then joins a group, by a new group or a group's new relay, is not kept from then on: the
    // warning shows again, also after the group is gone.
    [Fact]
    public async Task Should_ForgetAKeptAutoOn_WhenItsRelayJoinsAGroup()
    {
        var auto2 = new OwnRule(2, OwnRuleKind.AutoOn);
        var board = Board();
        await using var app = App(board);
        await RulesAsync(app, board, auto2, Timer3);
        var handler = app.Services.GetRequiredService<OwnRulesHandler>();
        Assert.Null(await handler.KeepAsync(BoardId, 2, OwnRuleKind.AutoOn, John, Cancel));
        Assert.Null(await handler.KeepAsync(BoardId, 3, OwnRuleKind.Timer, John, Cancel));
        Assert.Empty(handler.Warnings(BoardId));

        // A new group with relays 1 and 4, then relay 2 joins it.
        var groups = app.Services.GetRequiredService<GroupsHandler>();
        var (id, error) = await groups.CreateAsync(new GroupDraft("Fan heater", GroupIcons.Fan, 120, [new GroupStepDraft("Fan", [1]), new GroupStepDraft("Heat", [4])]), John, Cancel);
        Assert.Null(error);
        Assert.Empty(handler.Warnings(BoardId));
        Assert.Null(await groups.SaveAsync(id!.Value, new GroupDraft("Fan heater", GroupIcons.Fan, 120, [new GroupStepDraft("Fan", [1]), new GroupStepDraft("Heat", [2])]), John, Cancel));

        Assert.Equal([auto2], handler.Warnings(BoardId));
        Assert.Contains("The board turns a relay on again by itself", Box(await PageAsync(app, "/")), StringComparison.Ordinal);
        await using (var db = await Factory(app).CreateDbContextAsync(Cancel))
        {
            var rows = await db.BoardOwnRules.AsNoTracking().OrderBy(r => r.Channel).ToListAsync(Cancel);
            Assert.Equal([(2, false), (3, true)], rows.Select(r => (r.Channel, r.KeptUtc is not null)));
            Assert.Null(rows[0].KeptBy);
        }

        // The group goes: the warning stays until someone keeps it again.
        Assert.Null(await groups.DeleteAsync(id.Value, John, Cancel));
        Assert.Equal([auto2], handler.Warnings(BoardId));
        Assert.Null(await handler.KeepAsync(BoardId, 2, OwnRuleKind.AutoOn, John, Cancel));
        Assert.Empty(handler.Warnings(BoardId));
    }

    // What goes together changes while Relays is open (the board's rules stay the same): the warning follows it.
    [Fact]
    public async Task Should_RedrawRelays_WhenWhatGoesTogetherChanges()
    {
        var board = Board();
        await using var app = App(board);
        await RulesAsync(app, board, Timer3, Timer5);
        Assert.Null(await app.Services.GetRequiredService<OwnRulesHandler>().KeepAsync(BoardId, 5, OwnRuleKind.Timer, John, Cancel));
        using var page = await PageRenderer.StartAsync<Relays>(app.Services, John, "John");
        await Wait.UntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains("It can switch relay 3 without Contactor.", StringComparison.Ordinal));
        Assert.DoesNotContain("Also", await page.TextAsync(), StringComparison.Ordinal);

        board.SetOwnRules(Linked());

        await Wait.UntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains("Also turns off the board's timer for Relay 5.", StringComparison.Ordinal));
        Assert.Empty(page.Errors);
    }

    // What goes together changes while the page is open (the board's rules stay the same): the card follows it.
    [Fact]
    public async Task Should_RedrawTheCard_WhenWhatGoesTogetherChanges()
    {
        var board = Board();
        await using var app = App(board);
        await RulesAsync(app, board, Timer3, Timer5);
        using var page = await PageRenderer.StartAsync<BoardPage>(app.Services, John, "John", new Dictionary<string, object?> { [nameof(BoardPage.Id)] = BoardId });
        await Wait.UntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains("The board's own timers", StringComparison.Ordinal));
        Assert.DoesNotContain("Also", await page.TextAsync(), StringComparison.Ordinal);

        board.SetOwnRules(Linked());

        await Wait.UntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains("Also turns off the board's timer for Relay 5.", StringComparison.Ordinal));
        Assert.Empty(page.Errors);
    }

    [Fact]
    public async Task Should_ShowNothingOnRelays_WhenEveryRuleIsKept_OrWithoutContact()
    {
        var board = Board();
        await using var app = App(board);
        await RulesAsync(app, board, Timer3, Timer5);
        var handler = app.Services.GetRequiredService<OwnRulesHandler>();

        // One kept: the other is still warned about, in the singular.
        Assert.Null(await handler.KeepAsync(BoardId, 5, OwnRuleKind.Timer, John, Cancel));
        var one = await PageAsync(app, "/");
        Assert.Contains("It can switch relay 3 without Contactor.", one, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(one, "own-rule-line"));

        Assert.Null(await handler.KeepAsync(BoardId, 3, OwnRuleKind.Timer, John, Cancel));
        var kept = await PageAsync(app, "/");
        Assert.DoesNotContain("own-rule", kept, StringComparison.Ordinal);

        // Without contact, nothing: the rules the board last reported may be stale.
        board.SetOwnRules(new OwnRules([Timer3, Timer5, new OwnRule(7, OwnRuleKind.Timer)]));
        await Wait.UntilAsync(() => Service(app).Rows(BoardId).Count == 3);
        Assert.Contains("own-rules", await PageAsync(app, "/"), StringComparison.Ordinal);
        board.SetConnected(false);
        var offline = await PageAsync(app, "/");
        Assert.DoesNotContain("own-rule", offline, StringComparison.Ordinal);
        Assert.Contains("No contact with the relay board", offline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ShowNoButton_ForABoardThatCantTurnItsRulesOff()
    {
        var board = Board(new OwnRulesAbility(Reads: true, TurnsOff: false, MayHaveUnseen: false));
        await using var app = App(board);
        await RulesAsync(app, board, Timer3);

        var html = await PageAsync(app, "/");

        Assert.Contains("The board has a timer of its own", Box(html), StringComparison.Ordinal);
        Assert.DoesNotContain("<button", Box(html), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ShowNothingOnRelays_ForABoardThatCantTell()
    {
        await using var app = App(Board(OwnRulesAbility.CantTell));

        Assert.DoesNotContain("own-rule", await PageAsync(app, "/"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_TurnTheRulesOff_AndLogIt_WhenThePersonPresses()
    {
        var board = Board();
        await using var app = App(board);
        await RulesAsync(app, board, Timer3, Timer5);
        using var page = await PageRenderer.StartAsync<Relays>(app.Services, John, "John");
        Assert.Contains("The board has timers of its own", await page.TextAsync(), StringComparison.Ordinal);

        await page.ClickAsync(PageRenderer.WithText("Turn them off"));

        Assert.Equal(OwnRules.Empty, board.State.OwnRules);
        await using (var db = await Factory(app).CreateDbContextAsync(Cancel))
        {
            var entry = await db.EventLog.SingleAsync(e => e.Action == EventActions.BoardOwnRulesTurnedOff, Cancel);
            Assert.Equal((John, BoardId), (entry.Actor, entry.BoardId));
        }

        await Wait.UntilAsync(() => !page.TextAsync().GetAwaiter().GetResult().Contains("timers of its own", StringComparison.Ordinal));
        Assert.DoesNotContain("The board's timer can switch it", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(page.Errors);
    }

    [Fact]
    public async Task Should_SayWhy_InTheWarning_WhenTheBoardDoesntTurnThemOff()
    {
        var board = Board();
        await using var app = App(board);
        await RulesAsync(app, board, Timer3);
        using var page = await PageRenderer.StartAsync<Relays>(app.Services, John, "John");
        board.RefuseOwnRules = true;

        await page.ClickAsync(PageRenderer.WithText("Turn it off"));

        Assert.True(await page.HasAsync(a => PageRenderer.With("class", "rules-error")(a) && PageRenderer.WithText("The relay board didn't turn them off. Try again.")(a)));
        Assert.Equal(new OwnRules([Timer3]), board.State.OwnRules);
        Assert.True(await page.HasAsync(PageRenderer.WithText("Turn it off")));
        Assert.False(await page.HasAsync(a => PageRenderer.WithText("Turn it off")(a) && a.ContainsKey("disabled")));
        Assert.Empty(page.Errors);
    }

    [Fact]
    public async Task Should_WarnInEachBoardsSection_WithSeveralBoards()
    {
        var barn = new FakeRelayBoard(relayCount: 4, inputCount: 2, ownRules: OwnRulesAbility.ReadsAndTurnsOff);
        await using var boards = await TwoBoards.StartAsync(barn);
        barn.SetOwnRules(new OwnRules([new OwnRule(1, OwnRuleKind.Timer), new OwnRule(3, OwnRuleKind.Timer)]));
        await Wait.UntilAsync(() => Service(boards.App).Rows(boards.BarnId).Count == 2);
        int barnLight;
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            barnLight = await db.Relays.Where(r => r.BoardId == boards.BarnId && r.Channel == 1).Select(r => r.Id).SingleAsync(Cancel);
        }

        var html = await PageAsync(boards.App, "/");

        var section = html[html.IndexOf($"id=\"board-{boards.BarnId}\"", StringComparison.Ordinal)..];
        Assert.Contains("Barn has timers of its own", Box(section), StringComparison.Ordinal);
        Assert.Contains("They can switch relays 1 and 3 without Contactor.", Box(section), StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(html, "class=\"offline own-rules\""));
        Assert.Contains("The board's timer can switch it", CardText(html, $"relay/{barnLight}"), StringComparison.Ordinal);
        Assert.DoesNotContain("The board's timer", CardText(html, "relay/1"), StringComparison.Ordinal);
    }

    // A board's own rules reported again as they were, or changed only by a rule a person kept: nothing the page shows.
    [Fact]
    public async Task Should_RedrawRelays_OnlyWhenTheWarningsChange()
    {
        var board = Board();

        // A clock no poller moves: nothing else redraws the page meanwhile.
        await using var app = App(board, new ManualTimeProvider(NowUtc));
        await RulesAsync(app, board, Timer3, Timer5);
        Assert.Null(await app.Services.GetRequiredService<OwnRulesHandler>().KeepAsync(BoardId, 5, OwnRuleKind.Timer, John, Cancel));
        using var page = await PageRenderer.StartAsync<Relays>(app.Services, John, "John");
        Assert.Contains("It can switch relay 3 without Contactor.", await page.TextAsync(), StringComparison.Ordinal);

        Assert.False(await RedrawsAsync(page, () => board.SetOwnRules(new OwnRules([Timer3, Timer5]))));
        Assert.False(await RedrawsAsync(page, () => board.SetOwnRules(new OwnRules([Timer5, Timer3]))));

        // The kept rule gone: the service deletes its row and says so later (Changed), which shows nothing new either.
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, int boardId) => changed.TrySetResult();
        Service(app).Changed += OnChanged;
        try
        {
            var renders = page.PageRenders;
            Assert.False(await RedrawsAsync(page, () => board.SetOwnRules(new OwnRules([Timer3]))));
            await changed.Task.WaitAsync(Cancel);

            // The page's handler came first and is on the page's thread now: one more call there follows it.
            await page.Dispatcher.InvokeAsync(() => { });
            Assert.Single(Service(app).Rows(BoardId));
            Assert.Equal(renders, page.PageRenders);
        }
        finally
        {
            Service(app).Changed -= OnChanged;
        }

        // A new rule, and relay 5's again, which lost its "Keep" when it went: both are warned about.
        var seven = new OwnRule(7, OwnRuleKind.Timer);
        board.SetOwnRules(new OwnRules([Timer3, Timer5, seven]));
        await Wait.UntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains("relays 3, 5 and 7", StringComparison.Ordinal));
        Assert.Empty(page.Errors);
    }

    [Fact]
    public async Task Should_ShowTheRulesCard_OnTheBoardsPage_AndOnSettings()
    {
        var board = Board();
        await using var app = App(board, new ManualTimeProvider(NowUtc));
        await RulesAsync(app, board, Timer3, Timer5);
        Assert.Null(await app.Services.GetRequiredService<OwnRulesHandler>().KeepAsync(BoardId, 5, OwnRuleKind.Timer, John, Cancel));
        await using (var db = await Factory(app).CreateDbContextAsync(Cancel))
        {
            await db.Relays.Where(r => r.BoardId == BoardId && r.Channel == 3).ExecuteUpdateAsync(r => r.SetProperty(x => x.Name, "Fan heater"), Cancel);
        }

        foreach (var path in new[] { $"/board/{BoardId}", "/settings" })
        {
            var card = RulesCard(await PageAsync(app, path));

            Assert.Contains("<h2", card, StringComparison.Ordinal);
            Assert.Contains("The board's own timers", card, StringComparison.Ordinal);
            Assert.Contains("They can turn relays on and off without Contactor.", card, StringComparison.Ordinal);
            var rows = Regex.Split(card, "<li[^>]*>").Skip(1).Select(r => WebUtility.HtmlDecode(Tags().Replace(r, " "))).Select(r => Regex.Replace(r, "\\s+", " ").Trim()).ToList();
            Assert.Equal(["RELAY 3 Fan heater Found today at 07:12 Turn off Keep", "RELAY 5 Relay 5 Kept on purpose Turn off"], rows);
            Assert.DoesNotContain("disabled", card, StringComparison.Ordinal);
            Assert.DoesNotContain("may have timers", card, StringComparison.Ordinal);
        }

        // Without contact the buttons wait for the board.
        board.SetConnected(false);
        Assert.Equal(3, Regex.Count(RulesCard(await PageAsync(app, $"/board/{BoardId}")), "disabled"));
    }

    [Fact]
    public async Task Should_ShowOneLine_ForABoardThatCantTell_AndNoCard_ForOneWithNone()
    {
        await using (var app = App(Board(OwnRulesAbility.CantTell)))
        {
            foreach (var path in new[] { $"/board/{BoardId}", "/settings" })
            {
                var card = RulesCard(await PageAsync(app, path));
                Assert.Contains("The board's own timers", card, StringComparison.Ordinal);
                Assert.Contains("This board may have timers of its own. Check its own web page.", card, StringComparison.Ordinal);
                Assert.DoesNotContain("<button", card, StringComparison.Ordinal);
            }
        }

        await using (var app = App(Board(OwnRulesAbility.None)))
        {
            Assert.DoesNotContain("own-rules-card", await PageAsync(app, $"/board/{BoardId}"), StringComparison.Ordinal);
            Assert.DoesNotContain("own-rules-card", await PageAsync(app, "/settings"), StringComparison.Ordinal);
        }

        // A board that reads its rules and has none: no card.
        var empty = Board();
        await using (var app = App(empty))
        {
            empty.SetOwnRules(OwnRules.Empty);
            await Wait.UntilAsync(() => Service(app).TryGetRows(BoardId, out _));
            Assert.DoesNotContain("own-rules-card", await PageAsync(app, $"/board/{BoardId}"), StringComparison.Ordinal);
        }
    }

    // A board that reads its rules but may have others it can't see (the Shelly): the rules, then "also", never the
    // line for a board that can't tell, which would deny the rules just listed.
    [Fact]
    public async Task Should_SayABoardThatReadsItsRules_MayAlsoHaveOthers()
    {
        const string Also = "This board may also have rules Contactor can't see. Check its own web page.";
        const string CantTell = "This board may have timers of its own. Check its own web page.";
        var board = Board(new OwnRulesAbility(Reads: true, TurnsOff: true, MayHaveUnseen: true));
        await using (var app = App(board))
        {
            await RulesAsync(app, board, new OwnRules([Timer3]));
            foreach (var path in new[] { $"/board/{BoardId}", "/settings" })
            {
                var card = RulesCard(await PageAsync(app, path));
                Assert.Contains(">Turn off</button>", card, StringComparison.Ordinal);
                Assert.Contains(Also, card, StringComparison.Ordinal);
                Assert.DoesNotContain(CantTell, card, StringComparison.Ordinal);
            }

            // None read: still the card, with the line alone.
            board.SetOwnRules(OwnRules.Empty);
            await Wait.UntilAsync(() => Service(app).Rows(BoardId).Count == 0);
            var empty = RulesCard(await PageAsync(app, $"/board/{BoardId}"));
            Assert.Contains(Also, empty, StringComparison.Ordinal);
            Assert.DoesNotContain("<button", empty, StringComparison.Ordinal);
        }

        using (TestCulture.Language("sv"))
        {
            Assert.Equal("Kortet kan också ha regler som Contactor inte ser. Kontrollera kortets egen webbsida.", TestTexts.Localizer["OwnRules.Card.MayHaveUnseen"].Value);
        }
    }

    // The VM208 reads its week schedules, but a pulse or timer started on its own page lives only in its memory and
    // can switch a relay hours later: its board page says so, under its rules and with none.
    [Fact]
    public async Task Should_SayTheVm208_MayAlsoHaveRulesTheAppCantSee()
    {
        const string Also = "This board may also have rules Contactor can't see. Check its own web page.";
        var board = Board(Vm208Board.Vm208Capabilities.OwnRules);
        await using var app = App(board);
        await RulesAsync(app, board, new OwnRules([Timer3]));
        var card = RulesCard(await PageAsync(app, $"/board/{BoardId}"));
        Assert.Contains(">Turn off</button>", card, StringComparison.Ordinal);
        Assert.Contains(Also, card, StringComparison.Ordinal);

        board.SetOwnRules(OwnRules.Empty);
        await Wait.UntilAsync(() => Service(app).Rows(BoardId).Count == 0);
        Assert.Contains(Also, RulesCard(await PageAsync(app, $"/board/{BoardId}")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ShowOnlyKeep_ForABoardThatCantTurnItsRulesOff()
    {
        var board = Board(new OwnRulesAbility(Reads: true, TurnsOff: false, MayHaveUnseen: false));
        await using var app = App(board);
        await RulesAsync(app, board, Timer3);

        var card = RulesCard(await PageAsync(app, $"/board/{BoardId}"));

        Assert.Contains(">Keep</button>", card, StringComparison.Ordinal);
        Assert.DoesNotContain(">Turn off</button>", card, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_KeepAndTurnOff_FromTheBoardsPage()
    {
        var board = Board();
        await using var app = App(board);
        await RulesAsync(app, board, Timer3, Timer5);
        using var page = await PageRenderer.StartAsync<BoardPage>(app.Services, John, "John", new Dictionary<string, object?> { [nameof(BoardPage.Id)] = BoardId });
        await Wait.UntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains("The board's own timers", StringComparison.Ordinal));

        // The second row's "Keep": relay 5.
        await page.ClickAsync(PageRenderer.WithText("Keep"), 1);
        await Wait.UntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains("Kept on purpose", StringComparison.Ordinal));
        Assert.Equal(EventActions.BoardOwnRuleKept, (await EventsAsync(app)).Single().Action);

        // The first row's "Turn off": relay 3 only.
        await page.ClickAsync(PageRenderer.WithText("Turn off"));
        Assert.Equal(new OwnRules([Timer5]), board.State.OwnRules);
        await Wait.UntilAsync(() => Service(app).Rows(BoardId).Count == 1);
        await Wait.UntilAsync(() => !page.TextAsync().GetAwaiter().GetResult().Contains("RELAY 3", StringComparison.Ordinal));
        Assert.Equal([EventActions.BoardOwnRuleKept, EventActions.BoardOwnRulesTurnedOff], (await EventsAsync(app)).Select(e => e.Action));
        Assert.Empty(page.Errors);
    }

    // Another person keeps a rule: the open page follows the service's rows (Changed).
    [Fact]
    public async Task Should_FollowAnotherPersonsKeep_OnAnOpenRelaysPage()
    {
        var board = Board();
        await using var app = App(board);
        await RulesAsync(app, board, Timer3, Timer5);
        using var page = await PageRenderer.StartAsync<Relays>(app.Services, John, "John");
        Assert.Contains("They can switch relays 3 and 5", await page.TextAsync(), StringComparison.Ordinal);

        Assert.Null(await app.Services.GetRequiredService<OwnRulesHandler>().KeepAsync(BoardId, 5, OwnRuleKind.Timer, "anna@example.com", Cancel));

        await Wait.UntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains("It can switch relay 3 without Contactor.", StringComparison.Ordinal));
        Assert.Empty(page.Errors);
    }

    // Without contact nothing shows; when the board answers again, the warning comes back.
    [Fact]
    public async Task Should_ShowTheWarning_WhenTheBoardAnswersAgain()
    {
        var board = Board();
        await using var app = App(board);
        await RulesAsync(app, board, Timer3);
        board.SetConnected(false);
        using var page = await PageRenderer.StartAsync<Relays>(app.Services, John, "John");
        Assert.DoesNotContain("timer of its own", await page.TextAsync(), StringComparison.Ordinal);

        board.SetConnected(true);

        await Wait.UntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains("The board has a timer of its own", StringComparison.Ordinal));
        Assert.Contains("The board's timer can switch it", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(page.Errors);
    }

    // A group's relays have no card: the group's card names them by their role.
    [Fact]
    public async Task Should_NameTheGroupsRelays_OnItsCard()
    {
        var board = Board();
        await using var app = App(board);
        int groupId;
        await using (var db = await Factory(app).CreateDbContextAsync(Cancel))
        {
            var group = new RelayGroup { BoardId = BoardId, Name = "Fan heater", Icon = GroupIcons.Fan, MaxOnMinutes = 120 };
            group.AddStep("Fan", 1);
            group.AddStep("Heat", 3);
            await db.AddWithRuntimesAsync(group);
            groupId = group.Id;
        }

        await RulesAsync(app, board, Timer3);
        var heat = await PageAsync(app, "/");
        Assert.Contains("The board's timer can switch Heat", CardText(heat, $"group/{groupId}"), StringComparison.Ordinal);
        Assert.Contains("It can switch relay 3 without Contactor.", Box(heat), StringComparison.Ordinal);

        await RulesAsync(app, board, new OwnRule(1, OwnRuleKind.Timer), Timer3, Timer5);
        var both = await PageAsync(app, "/");
        Assert.Contains("The board's timer can switch Fan and Heat", CardText(both, $"group/{groupId}"), StringComparison.Ordinal);
        Assert.Contains("The board's timer can switch it", CardText(both, "relay/5"), StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(both, "own-rule-line"));
    }

    // Two changes close together: the card shows the latest rows, never those of a load that finished last.
    [Fact]
    public async Task Should_ShowTheLatestRows_WhenAnOlderLoadFinishesLast()
    {
        var board = Board();
        using var held = new HeldQueries();
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John },
            services =>
            {
                TestWebApp.WithBoard(board)(services);
                held.AddTo(services);
            });
        await RulesAsync(app, board, Timer3, Timer5);
        using var page = await PageRenderer.StartAsync<BoardPage>(app.Services, John, "John", new Dictionary<string, object?> { [nameof(BoardPage.Id)] = BoardId });
        await Wait.UntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains("RELAY 5", StringComparison.Ordinal));

        // The card's load after relay 5's rule goes waits for the database; then every rule goes.
        held.Hold(sql => sql.Contains("FROM \"Relay\"", StringComparison.Ordinal));
        board.SetOwnRules(new OwnRules([Timer3]));
        await Wait.UntilAsync(() => held.Holds > 0);
        board.SetOwnRules(OwnRules.Empty);
        await Wait.UntilAsync(() => !page.TextAsync().GetAwaiter().GetResult().Contains("The board's own timers", StringComparison.Ordinal));

        var renders = page.Renders;
        held.Release();
        await Wait.UntilAsync(() => page.Renders > renders);
        await page.Dispatcher.InvokeAsync(() => { });

        Assert.DoesNotContain("The board's own timers", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(page.Errors);
    }

    // Each page and the card unsubscribe when they close, or they would stay for the life of the app.
    [Fact]
    public async Task Should_LetGoOfTheEvents_WhenThePageCloses()
    {
        var board = Board();
        await using var app = App(board);
        await RulesAsync(app, board, Timer3);
        var registry = app.Services.GetRequiredService<Contactor.Web.Features.Boards.BoardRegistry>();
        var relays = await PageRenderer.StartAsync<Relays>(app.Services, John, "John");
        var boardPage = await PageRenderer.StartAsync<BoardPage>(app.Services, John, "John", new Dictionary<string, object?> { [nameof(BoardPage.Id)] = BoardId });
        await Wait.UntilAsync(() => boardPage.TextAsync().GetAwaiter().GetResult().Contains("The board's own timers", StringComparison.Ordinal));
        Assert.Equal(1, PageRenderer.Handlers<Relays>(registry, nameof(registry.OwnRulesChanged)));
        Assert.Equal(1, PageRenderer.Handlers<OwnRulesCard>(Service(app), nameof(OwnRulesService.Changed)));

        relays.Close();
        boardPage.Close();

        Assert.Equal(0, PageRenderer.Handlers<Relays>(registry, nameof(registry.OwnRulesChanged)));
        Assert.Equal(0, PageRenderer.Handlers<Relays>(Service(app), nameof(OwnRulesService.Changed)));
        Assert.Equal(0, PageRenderer.Handlers<OwnRulesCard>(Service(app), nameof(OwnRulesService.Changed)));
        Assert.Equal(0, PageRenderer.Handlers<OwnRulesCard>(registry, nameof(registry.StateChanged)));
        Assert.Equal(0, PageRenderer.Handlers<OwnRulesCard>(registry, nameof(registry.OwnRulesChanged)));
    }

    private static FakeRelayBoard Board(OwnRulesAbility? ability = null)
    {
        return new FakeRelayBoard(ownRules: ability ?? OwnRulesAbility.ReadsAndTurnsOff);
    }

    private static TestWebApp App(FakeRelayBoard board, TimeProvider? time = null)
    {
        return new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John },
            services =>
            {
                TestWebApp.WithBoard(board)(services);
                if (time is not null)
                {
                    services.AddSingleton(time);
                }
            });
    }

    private static OwnRulesService Service(TestWebApp app)
    {
        return app.Services.GetRequiredService<OwnRulesService>();
    }

    private static IDbContextFactory<ContactorDbContext> Factory(TestWebApp app)
    {
        return app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
    }

    // The board reports the rules, and the app has read them.
    private static Task RulesAsync(TestWebApp app, FakeRelayBoard board, params OwnRule[] rules)
    {
        return RulesAsync(app, board, new OwnRules(rules));
    }

    private static async Task RulesAsync(TestWebApp app, FakeRelayBoard board, OwnRules rules)
    {
        board.SetOwnRules(rules);
        await Wait.UntilAsync(() => Service(app).Rows(BoardId).Count == rules.Count);
    }

    // Relays 3 and 5 have timers: one schedule switches both, and another relay 3 alone. Turning off relay 3's takes
    // relay 5's with it, but not the other way round.
    private static OwnRules Linked()
    {
        return new OwnRules([Timer3, Timer5], [[Timer3, Timer5], [Timer3]]);
    }

    // A group of relays 1 (Fan) and 2 (Heat).
    private static Task<(int? Id, string? Error)> CreateGroupAsync(TestWebApp app)
    {
        return app.Services.GetRequiredService<GroupsHandler>().CreateAsync(
            new GroupDraft("Fan heater", GroupIcons.Fan, 120, [new GroupStepDraft("Fan", [1]), new GroupStepDraft("Heat", [2])]),
            John,
            Cancel);
    }

    private static async Task NameAsync(TestWebApp app, int channel, string name)
    {
        await using var db = await Factory(app).CreateDbContextAsync(Cancel);
        await db.Relays.Where(r => r.BoardId == BoardId && r.Channel == channel).ExecuteUpdateAsync(r => r.SetProperty(x => x.Name, name), Cancel);
    }

    private static async Task<List<EventLogEntry>> EventsAsync(TestWebApp app)
    {
        await using var db = await Factory(app).CreateDbContextAsync(Cancel);
        return await db.EventLog.AsNoTracking()
            .Where(e => e.Action == EventActions.BoardOwnRuleKept || e.Action == EventActions.BoardOwnRulesTurnedOff)
            .OrderBy(e => e.Id).ToListAsync(Cancel);
    }

    private static async Task<string> PageAsync(TestWebApp app, string path)
    {
        using var http = app.CreateClient();
        return WebUtility.HtmlDecode(await http.GetStringAsync(path, Cancel));
    }

    // The warning box, up to the cards that follow it.
    private static string Box(string html)
    {
        var start = html.IndexOf("class=\"offline own-rules\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "No warning.");
        var end = BoxEnds.Select(t => html.IndexOf(t, start, StringComparison.Ordinal)).Where(i => i >= 0).Min();
        return html[start..end];
    }

    // The board's own rules card on the board's page or Settings.
    private static string RulesCard(string html)
    {
        var start = html.IndexOf("own-rules-card", StringComparison.Ordinal);
        Assert.True(start >= 0, "No card.");
        return html[start..html.IndexOf("</section>", start, StringComparison.Ordinal)];
    }

    private static string Card(string html, string href)
    {
        var start = html.IndexOf($"href=\"{href}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No card for {href}.");
        var end = html.IndexOf("class=\"card", start, StringComparison.Ordinal);
        return html[start..(end < 0 ? html.Length : end)];
    }

    private static string CardText(string html, string href)
    {
        return Tags().Replace(Card(html, href), "");
    }

    // Whether a change the board reports, made on the page's own thread, redraws the page at once.
    private static Task<bool> RedrawsAsync(PageRenderer page, Action change)
    {
        return page.Dispatcher.InvokeAsync(() =>
        {
            var renders = page.PageRenders;
            change();
            return page.PageRenders != renders;
        });
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
