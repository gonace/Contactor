using System.Net;
using System.Text.RegularExpressions;
using Contactor.Boards;
using Contactor.Tests.Boards;
using Contactor.Web.Components;
using Contactor.Web.Features.BoardRules;
using Contactor.Web.Features.Groups;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// A board's own rules on the screens (issue #497): the red warning on Relays with "Turn them all off", a line and
/// "Turn off the timer" on each relay they can switch (issue #616), and the card on the board's page and on Settings
/// with one board.
/// </summary>
public sealed partial class OwnRulesPagesTests
{
    private const string John = "john@example.com";
    private const int BoardId = 1;

    private static readonly OwnRule Timer3 = new(3, OwnRuleKind.Timer);
    private static readonly OwnRule Timer5 = new(5, OwnRuleKind.Timer);

    // What follows the warning: the cards with one board, a board section's headings with several.
    private static readonly string[] BoxEnds = ["<main", "<h3"];

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_WarnOnRelays_AndOnEachRelayTheTimersCanSwitch()
    {
        var board = Board();
        await using var app = App(board);
        await RulesAsync(app, board, Timer3, Timer5);

        var html = await PageAsync(app, "/");

        var box = Box(html);
        Assert.Contains("The board has timers of its own", box, StringComparison.Ordinal);
        Assert.Contains("They can switch relays 3 and 5 on and off without Contactor.", box, StringComparison.Ordinal);
        Assert.Contains(">Turn them all off</button>", box, StringComparison.Ordinal);

        // Under the connection line, before the cards.
        Assert.True(html.IndexOf("class=\"online\"", StringComparison.Ordinal) < html.IndexOf("own-rules", StringComparison.Ordinal));
        Assert.True(html.IndexOf("own-rules", StringComparison.Ordinal) < html.IndexOf("href=\"relay/1\"", StringComparison.Ordinal));

        // Each relay's card has its line and its own button.
        foreach (var relay in new[] { "relay/3", "relay/5" })
        {
            Assert.Contains("The relay is also run by a timer on the board", CardText(html, relay), StringComparison.Ordinal);
            Assert.Contains(">Turn off the timer</button>", Card(html, relay), StringComparison.Ordinal);
        }

        Assert.DoesNotContain("timer", CardText(html, "relay/4"), StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(html, "own-rule-line"));

        // A board whose rules go alone says nothing more.
        Assert.DoesNotContain("Also", box, StringComparison.Ordinal);
    }

    // Relays 3 and 5 have timers (issue #616): relay 5's "Turn off the timer" turns off only relay 5's, logged; relay 3's
    // stays and is still warned about. A press the board refuses says so on that relay's card, not in the warning.
    [Fact]
    public async Task Should_TurnOffOnlyThatRelaysTimer_FromItsCard()
    {
        const string NotTaken = "The relay board didn't turn them off. Try again.";
        var board = Board();
        await using var app = App(board);
        await NameAsync(app, 5, "Heat");
        await RulesAsync(app, board, Timer3, Timer5);
        using var page = await PageRenderer.StartAsync<Relays>(app.Services, John, "John");
        var timer5 = PageRenderer.With("aria-label", "Turn off the timer for relay 5 (Heat)");

        board.RefuseOwnRules = true;
        await page.ClickAsync(timer5);
        var text = await page.TextAsync();
        Assert.Single(Regex.Matches(text, Regex.Escape(NotTaken)));
        Assert.True(text.IndexOf(NotTaken, StringComparison.Ordinal) > text.IndexOf("Heat", StringComparison.Ordinal));
        Assert.Empty(await EventsAsync(app));

        board.RefuseOwnRules = false;
        await page.ClickAsync(timer5);
        Assert.Equal(new OwnRules([Timer3]), board.State.OwnRules);
        var entry = (await EventsAsync(app)).Single(e => e.Action == EventActions.BoardOwnRulesTurnedOff);
        Assert.Contains("\"relays\":[5]", entry.Details, StringComparison.Ordinal);
        await Wait.UntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains("It can switch relay 3 on and off without Contactor.", StringComparison.Ordinal));
        Assert.True(await page.HasAsync(PageRenderer.WithText("Turn off the timer")));
        Assert.False(await page.HasAsync(timer5));
        Assert.Empty(page.Errors);
    }

    // A rule of another kind (relay 5's auto-on) gets no button on its card, where a "Turn off" next to ON/OFF could be
    // taken for turning the relay off: it stays on the warning's button, which turns it off.
    [Fact]
    public async Task Should_OfferNoCardButton_ForARuleThatIsntATimer()
    {
        var auto5 = new OwnRule(5, OwnRuleKind.AutoOn);
        var board = Board();
        await using var app = App(board);
        await RulesAsync(app, board, auto5);

        var html = await PageAsync(app, "/");
        Assert.DoesNotContain("turn-off-rule", Card(html, "relay/5"), StringComparison.Ordinal);
        Assert.Contains("The board turns it on again by itself after it goes off", CardText(html, "relay/5"), StringComparison.Ordinal);
        Assert.Contains(">Turn it off</button>", Box(html), StringComparison.Ordinal);

        using var page = await PageRenderer.StartAsync<Relays>(app.Services, John, "John");
        await page.ClickAsync(PageRenderer.WithText("Turn it off"));
        Assert.Equal(OwnRules.Empty, board.State.OwnRules);
        Assert.Empty(page.Errors);
    }

    // Relay 3's timer is a schedule on the board that also switches relay 5 (Heat), which a person kept: with one relay,
    // the warning has no button, and relay 3's card says before the press that "Turn off the timer" takes Heat's too,
    // and it does.
    [Fact]
    public async Task Should_SayOnRelays_WhatGoesWithTheTurnOff_AndTurnItOff()
    {
        var board = Board();
        await using var app = App(board);
        await NameAsync(app, 5, "Heat");
        await RulesAsync(app, board, Linked());
        Assert.Null(await app.Services.GetRequiredService<OwnRulesHandler>().KeepAsync(BoardId, 5, OwnRuleKind.Timer, John, Cancel));

        var html = await PageAsync(app, "/");
        var box = Box(html);
        Assert.Contains("It can switch relay 3 on and off without Contactor.", box, StringComparison.Ordinal);
        Assert.DoesNotContain("</button>", box, StringComparison.Ordinal);
        Assert.DoesNotContain("Also", box, StringComparison.Ordinal);
        var card = Card(html, "relay/3");
        Assert.Contains("Also turns off the board's timer for Heat.", WebUtility.HtmlDecode(card), StringComparison.Ordinal);
        Assert.True(card.IndexOf("Also turns off", StringComparison.Ordinal) < card.IndexOf(">Turn off the timer</button>", StringComparison.Ordinal));
        Assert.DoesNotContain("Turn off the timer", Card(html, "relay/5"), StringComparison.Ordinal);

        using var page = await PageRenderer.StartAsync<Relays>(app.Services, John, "John");
        await page.ClickAsync(PageRenderer.WithText("Turn off the timer"));

        Assert.Equal(OwnRules.Empty, board.State.OwnRules);
        await Wait.UntilAsync(() => Service(app).Rows(BoardId).Count == 0);
        var entry = (await EventsAsync(app)).Single(e => e.Action == EventActions.BoardOwnRulesTurnedOff);
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
        const string Why = "Can't be kept: Heat is in a group, and the board would switch it by itself without the group's order.";

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

        // Heat has no card of its own: its one rule keeps the warning's button (issue #616).
        var box = Box(await PageAsync(app, "/"));
        Assert.Contains("The board turns a relay on again by itself", box, StringComparison.Ordinal);
        Assert.Contains(">Turn it off</button>", box, StringComparison.Ordinal);
        Assert.Equal(2, (await EventsAsync(app)).Count(e => e.Action == EventActions.BoardOwnRuleKept));

        using var swedish = TestCulture.Language("sv");
        Assert.Equal("Kan inte behållas: Värme ingår i en grupp, och kortet skulle slå om det självt utan gruppens ordning.", TestTexts.Localizer["OwnRules.Keep.InGroup", "Värme"].Value);
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

    [Fact]
    public async Task Should_TurnTheRulesOff_AndLogIt_WhenThePersonPresses()
    {
        var board = Board();
        await using var app = App(board);
        await RulesAsync(app, board, Timer3, Timer5);
        using var page = await PageRenderer.StartAsync<Relays>(app.Services, John, "John");
        Assert.Contains("The board has timers of its own", await page.TextAsync(), StringComparison.Ordinal);

        await page.ClickAsync(PageRenderer.WithText("Turn them all off"));

        Assert.Equal(OwnRules.Empty, board.State.OwnRules);
        await using (var db = await Factory(app).CreateDbContextAsync(Cancel))
        {
            var entry = await db.EventLog.SingleAsync(e => e.Action == EventActions.BoardOwnRulesTurnedOff, Cancel);
            Assert.Equal((John, BoardId), (entry.Actor, entry.BoardId));
        }

        await Wait.UntilAsync(() => !page.TextAsync().GetAwaiter().GetResult().Contains("timers of its own", StringComparison.Ordinal));
        Assert.DoesNotContain("run by a timer on the board", await page.TextAsync(), StringComparison.Ordinal);
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
        Assert.Contains("They can switch relays 1 and 3 on and off without Contactor.", Box(section), StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(html, "class=\"offline own-rules\""));
        Assert.Contains("The relay is also run by a timer on the board", CardText(html, $"relay/{barnLight}"), StringComparison.Ordinal);
        Assert.DoesNotContain("timer", CardText(html, "relay/1"), StringComparison.Ordinal);
    }

    // A board that answers and reads its rules but couldn't read them (issue #551): the card says so, and goes when a
    // reading comes; nothing before the board's first answer or while it doesn't answer.
    [Fact]
    public async Task Should_SayTheRulesCouldntBeRead_WhileTheBoardAnswersWithoutThem()
    {
        const string CouldntRead = "Contactor couldn't read this board's own rules. Check its own web page.";
        var board = new FakeRelayBoard(connected: false, ownRules: OwnRulesAbility.ReadsAndTurnsOff);
        await using var app = App(board);

        // Not answered yet: its rules are only not known.
        Assert.DoesNotContain("own-rules-card", await PageAsync(app, $"/board/{BoardId}"), StringComparison.Ordinal);

        board.SetConnected(true);
        foreach (var path in new[] { $"/board/{BoardId}", "/settings" })
        {
            var card = RulesCard(await PageAsync(app, path));
            Assert.Contains(CouldntRead, card, StringComparison.Ordinal);
            Assert.DoesNotContain("<button", card, StringComparison.Ordinal);
        }

        // Nothing is sent to the board, and nothing is noted.
        Assert.Equal(0, board.OwnRulesTurnOffs);
        Assert.Empty(board.Sent);
        Assert.Empty(Service(app).Rows(BoardId));

        // While it doesn't answer: nothing.
        board.SetConnected(false);
        Assert.DoesNotContain("own-rules-card", await PageAsync(app, $"/board/{BoardId}"), StringComparison.Ordinal);

        // A reading: its rows, without the line.
        board.SetConnected(true);
        await RulesAsync(app, board, Timer3);
        Assert.DoesNotContain(CouldntRead, RulesCard(await PageAsync(app, $"/board/{BoardId}")), StringComparison.Ordinal);

        // Unknown again: the line, and the rows from before stay as they are.
        board.SetOwnRules(null);
        Assert.Contains(CouldntRead, RulesCard(await PageAsync(app, $"/board/{BoardId}")), StringComparison.Ordinal);
        Assert.Single(Service(app).Rows(BoardId));
    }

    // The open card follows: the line comes when the rules become unknown and goes at the next reading, also when only
    // the rules changed.
    [Fact]
    public async Task Should_RedrawTheCard_WhenTheRulesCantBeReadAndComeBack()
    {
        const string CouldntRead = "Contactor couldn't read this board's own rules. Check its own web page.";
        var board = Board();
        await using var app = App(board);
        await RulesAsync(app, board, Timer3);
        using var page = await PageRenderer.StartAsync<BoardPage>(app.Services, John, "John", new Dictionary<string, object?> { [nameof(BoardPage.Id)] = BoardId });
        await Wait.UntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains("The board's own timers", StringComparison.Ordinal));
        Assert.DoesNotContain(CouldntRead, await page.TextAsync(), StringComparison.Ordinal);

        board.SetOwnRules(null);
        await Wait.UntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains(CouldntRead, StringComparison.Ordinal));

        board.SetOwnRules(new OwnRules([Timer3]));
        await Wait.UntilAsync(() => !page.TextAsync().GetAwaiter().GetResult().Contains(CouldntRead, StringComparison.Ordinal));
        Assert.Empty(page.Errors);
    }

    // On a board that can't turn its rules off (e.g. a pulse started on the VM204's own page), nothing is left on the relay
    // once the run ends: the running group has no "Keep" and doesn't say when it could be pressed.
    [Fact]
    public async Task Should_OfferNoKeepAfterTheRun_OnABoardThatCantTurnItsRulesOff()
    {
        const string Later = "Once the pulse or timer has ended, a schedule on the relay can be turned off or kept.";
        var board = Board(new OwnRulesAbility(Reads: true, TurnsOff: false, MayHaveUnseen: true));
        await using var app = App(board);
        await RulesAsync(app, board, new OwnRules([Timer3], [[Timer3]], [Timer3]));

        var box = Box(await PageAsync(app, "/"));
        var card = RulesCard(await PageAsync(app, $"/board/{BoardId}"));

        foreach (var shown in new[] { box, card })
        {
            Assert.Contains("Running on the board right now", shown, StringComparison.Ordinal);
            Assert.DoesNotContain(">Keep</button>", shown, StringComparison.Ordinal);
            Assert.DoesNotContain(Later, shown, StringComparison.Ordinal);
        }
    }

    // A rule the board runs now and ends by itself (relay 3, e.g. a timer started on the VM208's own page): warned about,
    // in a group of its own, "Running on the board right now", with no "Turn off", a "Keep" that can't be pressed and
    // when it can; the handler refuses its "Keep" too. Once the run has ended, the open card shows it as usual.
    [Fact]
    public async Task Should_OfferNoTurnOff_ForARuleTheBoardCantTurnOff()
    {
        const string CantTurnOff = "Started on the board's own web page. It ends by itself, and Contactor can't stop it.";
        var board = Board();
        await using var app = App(board);
        await RulesAsync(app, board, new OwnRules([Timer3, Timer5], null, [Timer3]));
        var handler = app.Services.GetRequiredService<OwnRulesHandler>();
        Assert.False(handler.CanTurnOff(BoardId, Timer3));
        Assert.Equal([Timer5], handler.TurnsOff(BoardId, [Timer3, Timer5]));

        var html = await PageAsync(app, "/");
        var box = Box(html);
        Assert.Contains("They can switch relays 3 and 5 on and off without Contactor.", box, StringComparison.Ordinal);
        Assert.DoesNotContain("Turn ", box, StringComparison.Ordinal);
        Assert.Contains("Running on the board right now", box, StringComparison.Ordinal);
        Assert.Single(DisabledKeep().Matches(box));

        // Only relay 5's card has "Turn off the timer"; relay 3 keeps its line, in the running group.
        Assert.Contains(">Turn off the timer</button>", Card(html, "relay/5"), StringComparison.Ordinal);
        Assert.DoesNotContain("Turn off the timer", Card(html, "relay/3"), StringComparison.Ordinal);
        Assert.Contains("The relay is also run by a timer on the board", CardText(html, "relay/3"), StringComparison.Ordinal);

        // Relay 5's "Turn off the timer" sends only relay 5's: relay 3's stays, unlogged, and still warned about.
        using (var relays = await PageRenderer.StartAsync<Relays>(app.Services, John, "John"))
        {
            await relays.ClickAsync(PageRenderer.WithText("Turn off the timer"));
            Assert.Equal(new OwnRules([Timer3], null, [Timer3]), board.State.OwnRules);
            Assert.Equal(1, board.OwnRulesTurnOffs);
            var entry = (await EventsAsync(app)).Single(e => e.Action == EventActions.BoardOwnRulesTurnedOff);
            Assert.Contains("\"relays\":[5]", entry.Details, StringComparison.Ordinal);
            await Wait.UntilAsync(() => Service(app).Rows(BoardId).Count == 1);
            Assert.Contains("It can switch relay 3 on and off without Contactor.", Box(await PageAsync(app, "/")), StringComparison.Ordinal);
            Assert.Empty(relays.Errors);
        }

        await RulesAsync(app, board, new OwnRules([Timer3, Timer5], null, [Timer3]));

        var card = RulesCard(await PageAsync(app, $"/board/{BoardId}"));
        Assert.Single(Regex.Matches(card, ">Turn off</button>"));
        Assert.Equal(2, Regex.Count(card, ">Keep</button>"));
        Assert.Single(DisabledKeep().Matches(card));
        Assert.True(card.IndexOf(">Turn off</button>", StringComparison.Ordinal) < card.IndexOf("Running on the board right now", StringComparison.Ordinal));
        Assert.Contains("Once the pulse or timer has ended, a schedule on the relay can be turned off or kept.", card, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(card, "Started on the board&#x27;s own web page\\. It ends by itself, and Contactor can|Started on the board's own web page\\. It ends by itself, and Contactor can"));

        using var page = await PageRenderer.StartAsync<BoardPage>(app.Services, John, "John", new Dictionary<string, object?> { [nameof(BoardPage.Id)] = BoardId });
        await Wait.UntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains(CantTurnOff, StringComparison.Ordinal));
        board.SetOwnRules(new OwnRules([Timer3, Timer5]));
        await Wait.UntilAsync(() => !page.TextAsync().GetAwaiter().GetResult().Contains(CantTurnOff, StringComparison.Ordinal));
        Assert.DoesNotContain("Running on the board right now", await page.TextAsync(), StringComparison.Ordinal);
        await page.ClickAsync(PageRenderer.WithText("Turn off"), 1);
        Assert.Equal(new OwnRules([Timer3]), board.State.OwnRules);

        // With only such a rule, the warning on Relays has no "Turn off", only the "Keep" that can't be pressed.
        board.SetOwnRules(new OwnRules([Timer3], null, [Timer3]));
        await Wait.UntilAsync(() => Service(app).Rows(BoardId).Count == 1);
        box = Box(await PageAsync(app, "/"));
        Assert.DoesNotContain("Turn ", box, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(box, "</button>"));
        Assert.Single(DisabledKeep().Matches(box));

        // A "Keep" sent while it runs (from a page drawn before it started) is refused, and nothing is saved or logged.
        Assert.Equal("Once the pulse or timer has ended, a schedule on the relay can be turned off or kept.", await handler.KeepAsync(BoardId, 3, OwnRuleKind.Timer, John, Cancel));
        Assert.Null(await handler.KeepAllAsync(BoardId, [Timer3], John, Cancel));
        Assert.All(Service(app).Rows(BoardId), r => Assert.Null(r.KeptUtc));
        Assert.DoesNotContain(await EventsAsync(app), e => e.Action == EventActions.BoardOwnRuleKept);

        // Once it has ended, it can be kept.
        board.SetOwnRules(new OwnRules([Timer3]));
        Assert.Null(await handler.KeepAsync(BoardId, 3, OwnRuleKind.Timer, John, Cancel));

        // A rule kept before a run starts stays in the list as kept, out of the running group.
        board.SetOwnRules(new OwnRules([Timer3], null, [Timer3]));
        await Wait.UntilAsync(() => Service(app).Rows(BoardId).Single().KeptUtc is not null);
        card = RulesCard(await PageAsync(app, $"/board/{BoardId}"));
        Assert.Contains("Kept on purpose", card, StringComparison.Ordinal);
        Assert.DoesNotContain("Running on the board right now", card, StringComparison.Ordinal);
        Assert.DoesNotContain("</button>", card, StringComparison.Ordinal);
        Assert.Empty(page.Errors);
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

        await Wait.UntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains("It can switch relay 3 on and off without Contactor.", StringComparison.Ordinal));
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
        Assert.Contains("The relay is also run by a timer on the board", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(page.Errors);
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

    // A "Keep" that can't be pressed, for a rule that runs on the board right now.
    [GeneratedRegex("<button[^>]*disabled[^>]*>Keep</button>")]
    private static partial Regex DisabledKeep();

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

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
