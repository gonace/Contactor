using System.Text.RegularExpressions;
using Contactor.Boards;
using Contactor.Boards.Velleman;
using Contactor.Tests.Boards;
using Contactor.Web.Components;
using Contactor.Web.Features.BoardRules;
using Contactor.Web.Features.Boards;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// "This board has timers of its own" (issue #497), the step after "Where is the board?" in "Add a board" and the setup
/// guide (<see cref="BoardOwnRulesStep"/>): shown only for a board that answers with rules no person kept, "Turn them
/// off" confirmed by the board's state or not, "Keep them", one command for a double press, the count of steps, and the
/// line for a board that can't tell. The board being added is a board in memory, run as a VM201.
/// </summary>
public sealed class BoardOwnRulesStepTests
{
    private const string John = "john@example.com";

    private static readonly OwnRule Timer3 = new(3, OwnRuleKind.Timer);
    private static readonly OwnRule Timer5 = new(5, OwnRuleKind.Timer);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_WordTheStep_InEnglishAndSwedish()
    {
        Assert.Equal("This board has timers of its own", TestTexts.OwnRules.StepTitle([Timer3, Timer5]));
        Assert.Equal("This board has a timer of its own", TestTexts.OwnRules.StepTitle([Timer3]));
        Assert.Equal("This board has rules of its own", TestTexts.OwnRules.StepTitle([Timer3, new OwnRule(5, OwnRuleKind.InputEdge)]));
        Assert.Equal("They can turn relays on and off without Contactor.", TestTexts.OwnRules.StepIntro(2));
        Assert.Equal("We recommend turning them off.", TestTexts.OwnRules.Recommend(2));
        Assert.Equal(("Turn them off", "Keep them"), (TestTexts.OwnRules.TurnOffAll(2), TestTexts.OwnRules.KeepAll(2)));
        Assert.Equal(("Turn it off", "Keep it"), (TestTexts.OwnRules.TurnOffAll(1), TestTexts.OwnRules.KeepAll(1)));

        using var swedish = TestCulture.Language("sv");
        Assert.Equal("Kortet har egna timers", TestTexts.OwnRules.StepTitle([Timer3, Timer5]));
        Assert.Equal("De kan slå på och stänga av reläer utan Contactor.", TestTexts.OwnRules.StepIntro(2));
        Assert.Equal("Vi rekommenderar att du stänger av dem.", TestTexts.OwnRules.Recommend(2));
        Assert.Equal("Vi rekommenderar att du stänger av den.", TestTexts.OwnRules.Recommend(1));
        Assert.Equal(("Stäng av dem", "Behåll dem"), (TestTexts.OwnRules.TurnOffAll(2), TestTexts.OwnRules.KeepAll(2)));
    }

    // The board answers with its timers on for relays 3 and 5: the step lists them by number (they have no names yet),
    // and "Turn them off" goes on once the board's state shows them gone, logged with the person.
    [Fact]
    public async Task Should_AskAboutTheTimers_AndGoOn_WhenTurningThemOffIsConfirmed()
    {
        var board = new FakeRelayBoard(ownRules: OwnRulesAbility.ReadsAndTurnsOff);
        await using var app = App();
        using var page = await PageRenderer.StartAsync<BoardNew>(app.Services, John, "John");
        var live = await ToWhereAsync(app, page, board, Timer3, Timer5);
        Assert.Contains("STEP 2 OF 4", await page.TextAsync(), StringComparison.Ordinal);

        await page.ClickAsync(Button("Next"));

        var text = await page.TextAsync();
        Assert.Contains("STEP 3 OF 4", text, StringComparison.Ordinal);
        Assert.Contains("This board has timers of its own", text, StringComparison.Ordinal);
        Assert.Contains("They can turn relays on and off without Contactor.", text, StringComparison.Ordinal);
        Assert.Contains("Relay 3", text, StringComparison.Ordinal);
        Assert.Contains("Relay 5", text, StringComparison.Ordinal);
        Assert.Contains("We recommend turning them off.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("doesn't switch", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Also", text, StringComparison.Ordinal);
        Assert.True(await page.HasAsync(Button("Keep them")));
        Assert.False(await page.HasAsync(Button("Next")));

        await page.ClickAsync(Button("Turn them off"));

        text = await page.TextAsync();
        Assert.Contains("Name your relays", text, StringComparison.Ordinal);
        Assert.Contains("STEP 4 OF 4", text, StringComparison.Ordinal);
        Assert.Equal(OwnRules.Empty, board.State.OwnRules);
        var logged = Assert.Single(await EventsAsync(app, live.BoardId, EventActions.BoardOwnRulesTurnedOff));
        Assert.Equal(John, logged.Actor);
        Assert.Contains("\"relays\":[3,5]", logged.Details, StringComparison.Ordinal);

        // Back from the relays: the step has nothing to ask any more, so "Where is the board?" again.
        await page.ClickAsync(Button("Back"));
        text = await page.TextAsync();
        Assert.Contains("Where is the board?", text, StringComparison.Ordinal);
        Assert.Contains("STEP 2 OF 3", text, StringComparison.Ordinal);
        Assert.Empty(Errors(page));
    }

    // The board takes the command, but its state keeps the timers: after 5 s on the app's clock the step says so and
    // stays, and "Turn them off" can be pressed again. A second press while one is under way sends nothing more.
    [Fact]
    public async Task Should_StayOnTheStep_WhenTheBoardDoesntConfirm_AndSendOnce_OnADoublePress()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        var board = new FakeRelayBoard(ownRules: OwnRulesAbility.ReadsAndTurnsOff) { HoldOwnRules = true };
        await using var app = App(time);
        using var page = await PageRenderer.StartAsync<BoardNew>(app.Services, John, "John");
        await ToWhereAsync(app, page, board, Timer3, Timer5);
        await page.ClickAsync(Button("Next"));
        Assert.Contains("This board has timers of its own", await page.TextAsync(), StringComparison.Ordinal);

        var first = page.ClickAsync(Button("Turn them off"));
        await Wait.UntilAsync(() => board.OwnRulesTurnOffs == 1);
        Assert.True(await DisabledAsync(page, Button("Turn them off")));
        await page.ClickAsync(Button("Turn them off"));
        await Wait.UntilAsync(() =>
        {
            if (first.IsCompleted)
            {
                return true;
            }

            // The wait for the board's state is armed after the command: moved a second at a time until it is over.
            time.Advance(TimeSpan.FromSeconds(1));
            return false;
        });
        await first;

        Assert.Equal(1, board.OwnRulesTurnOffs);
        var text = await page.TextAsync();
        Assert.Contains("The board still shows them on. Try again, or keep them.", text, StringComparison.Ordinal);
        Assert.Contains("This board has timers of its own", text, StringComparison.Ordinal);
        Assert.Contains("STEP 3 OF 4", text, StringComparison.Ordinal);
        Assert.False(await DisabledAsync(page, Button("Turn them off")));

        // "Try again": this time the board's state confirms it.
        board.HoldOwnRules = false;
        await page.ClickAsync(Button("Turn them off"));
        Assert.Equal(2, board.OwnRulesTurnOffs);
        Assert.Contains("Name your relays", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(Errors(page));
    }

    // "Keep them", as soon as the step shows: each is kept by the person, the board's timers stay on, and the rows are
    // still kept once the board is added ("Done").
    [Fact]
    public async Task Should_KeepTheRules_AndGoOn()
    {
        var board = new FakeRelayBoard(ownRules: OwnRulesAbility.ReadsAndTurnsOff);
        await using var app = App();
        using var page = await PageRenderer.StartAsync<BoardNew>(app.Services, John, "John");
        var live = await ToWhereAsync(app, page, board, Timer3, Timer5);
        await page.ClickAsync(Button("Next"));
        Assert.False(await DisabledAsync(page, Button("Keep them")));

        await page.ClickAsync(Button("Keep them"));

        var text = await page.TextAsync();
        Assert.Contains("Name your relays", text, StringComparison.Ordinal);
        Assert.Contains("STEP 4 OF 4", text, StringComparison.Ordinal);
        Assert.Equal(new OwnRules([Timer3, Timer5]), board.State.OwnRules);
        Assert.Equal(0, board.OwnRulesTurnOffs);

        await page.ClickAsync(Button("Done"));

        Assert.False(live.IsUnfinished);
        var rows = await RowsAsync(app, live.BoardId);
        Assert.Equal([3, 5], rows.Select(r => r.Channel));
        Assert.All(rows, r => Assert.Equal((true, John), (r.KeptUtc is not null, r.KeptBy)));
        Assert.Equal(2, (await EventsAsync(app, live.BoardId, EventActions.BoardOwnRuleKept)).Count);
        Assert.Empty(app.Services.GetRequiredService<OwnRulesHandler>().Warnings(live.BoardId));
    }

    // Relay 3's timer takes relay 5's with it, and a person kept relay 5's already: the step asks about relay 3 and says
    // first that turning it off takes relay 5's too. Both go, and the event names relay 3.
    [Fact]
    public async Task Should_SayWhatGoesWithTheTurnOff_OnTheStep()
    {
        var board = new FakeRelayBoard(ownRules: OwnRulesAbility.ReadsAndTurnsOff);
        await using var app = App();
        using var page = await PageRenderer.StartAsync<BoardNew>(app.Services, John, "John");
        var live = await ToWhereAsync(app, page, board);
        board.SetOwnRules(new OwnRules([Timer3, Timer5], [[Timer3, Timer5], [Timer3]]));
        Assert.Null(await app.Services.GetRequiredService<OwnRulesHandler>().KeepAllAsync(live.BoardId, [Timer5], John, Cancel));
        await page.ClickAsync(Button("Next"));

        var text = await page.TextAsync();
        Assert.Contains("This board has a timer of its own", text, StringComparison.Ordinal);
        Assert.Contains("Also turns off the board's timer for Relay 5.", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("Also turns off", StringComparison.Ordinal) < text.IndexOf("Turn it off", StringComparison.Ordinal));

        await page.ClickAsync(Button("Turn it off"));

        Assert.Contains("Name your relays", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Equal(OwnRules.Empty, board.State.OwnRules);
        var logged = Assert.Single(await EventsAsync(app, live.BoardId, EventActions.BoardOwnRulesTurnedOff));
        Assert.Contains("\"relays\":[3]", logged.Details, StringComparison.Ordinal);
        Assert.Contains("\"also\":[{\"relay\":5,\"kind\":\"timer\"}]", logged.Details, StringComparison.Ordinal);
        Assert.Empty(Errors(page));
    }

    // What goes together changes while the step shows (the rules asked stay the same): the step follows it.
    [Fact]
    public async Task Should_RedrawTheStep_WhenWhatGoesTogetherChanges()
    {
        var board = new FakeRelayBoard(ownRules: OwnRulesAbility.ReadsAndTurnsOff);
        await using var app = App();
        using var page = await PageRenderer.StartAsync<BoardNew>(app.Services, John, "John");
        var live = await ToWhereAsync(app, page, board, Timer3, Timer5);
        Assert.Null(await app.Services.GetRequiredService<OwnRulesHandler>().KeepAllAsync(live.BoardId, [Timer5], John, Cancel));
        await page.ClickAsync(Button("Next"));
        Assert.Contains("This board has a timer of its own", await page.TextAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("Also", await page.TextAsync(), StringComparison.Ordinal);

        board.SetOwnRules(new OwnRules([Timer3, Timer5], [[Timer3, Timer5], [Timer3]]));

        await Wait.UntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains("Also turns off the board's timer for Relay 5.", StringComparison.Ordinal));
        Assert.Empty(Errors(page));
    }

    // No step for a board with no rules of its own, for a model that has none (even if it reported some), or for a
    // board that doesn't answer: the count never shows a step that doesn't come.
    [Theory]
    [InlineData("none")]
    [InlineData("no rules")]
    [InlineData("offline")]
    public async Task Should_SkipTheStep(string why)
    {
        var board = why switch
        {
            "none" => new FakeRelayBoard(ownRules: OwnRulesAbility.None),
            "offline" => new FakeRelayBoard(connected: false, ownRules: OwnRulesAbility.ReadsAndTurnsOff),
            _ => new FakeRelayBoard(ownRules: OwnRulesAbility.ReadsAndTurnsOff),
        };
        OwnRule[] rules = why == "no rules" ? [] : [Timer3, Timer5];
        await using var app = App();
        using var page = await PageRenderer.StartAsync<BoardNew>(app.Services, John, "John");
        await ToWhereAsync(app, page, board, rules);
        Assert.Contains("STEP 2 OF 3", await page.TextAsync(), StringComparison.Ordinal);

        await page.ClickAsync(Button(why == "offline" ? "Go on without a connection" : "Next"));

        var text = await page.TextAsync();
        Assert.Contains("Name your relays", text, StringComparison.Ordinal);
        Assert.Contains("STEP 3 OF 3", text, StringComparison.Ordinal);
        Assert.DoesNotContain("of its own", text, StringComparison.Ordinal);

        await page.ClickAsync(Button("Back"));
        Assert.Contains("Where is the board?", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(Errors(page));
    }

    // A board that can't tell whether it has rules of its own gets no step, only a hint at the top of "Name your
    // relays"; a board that reads its rules gets none there.
    [Fact]
    public async Task Should_SayABoardThatCantTell_MayHaveTimers_OnNameYourRelays()
    {
        const string Line = "This board may have timers of its own. Check its own web page.";
        await using (var app = App())
        {
            using var page = await PageRenderer.StartAsync<BoardNew>(app.Services, John, "John");
            await ToWhereAsync(app, page, new FakeRelayBoard(ownRules: OwnRulesAbility.CantTell));
            Assert.DoesNotContain(Line, await page.TextAsync(), StringComparison.Ordinal);

            await page.ClickAsync(Button("Next"));
            var text = await page.TextAsync();
            Assert.Matches($"(?s)Name your relays.*{Line}.*RELAY 1", text);
            Assert.Contains("STEP 3 OF 3", text, StringComparison.Ordinal);
        }

        await using (var app = App())
        {
            using var page = await PageRenderer.StartAsync<BoardNew>(app.Services, John, "John");
            await ToWhereAsync(app, page, new FakeRelayBoard(ownRules: OwnRulesAbility.ReadsAndTurnsOff));
            await page.ClickAsync(Button("Next"));
            var text = await page.TextAsync();
            Assert.Contains("Name your relays", text, StringComparison.Ordinal);
            Assert.DoesNotContain(Line, text, StringComparison.Ordinal);
        }

        // A board that reads its rules but may have others it can't see: "also", not the line above.
        await using (var app = App())
        {
            using var page = await PageRenderer.StartAsync<BoardNew>(app.Services, John, "John");
            await ToWhereAsync(app, page, new FakeRelayBoard(ownRules: new OwnRulesAbility(Reads: true, TurnsOff: true, MayHaveUnseen: true)));
            await page.ClickAsync(Button("Next"));
            var text = await page.TextAsync();
            Assert.Matches("(?s)Name your relays.*This board may also have rules Contactor can't see\\. Check its own web page\\..*RELAY 1", text);
            Assert.DoesNotContain(Line, text, StringComparison.Ordinal);
        }

        using (TestCulture.Language("sv"))
        {
            Assert.Equal("Kortet kan ha egna timers. Titta på kortets egen webbsida.", TestTexts.Localizer["OwnRules.Card.CantTell"].Value);
        }
    }

    // A board that answers but couldn't read its rules (issue #551) gets no rules step, and "Name your relays" says so;
    // the line follows the board's rules alone, and goes while the board doesn't answer.
    [Fact]
    public async Task Should_SayTheRulesCouldntBeRead_OnNameYourRelays()
    {
        const string Line = "Contactor couldn't read this board's own rules. Check its own web page.";
        var board = new FakeRelayBoard(ownRules: OwnRulesAbility.ReadsAndTurnsOff);
        await using var app = App();
        using var page = await PageRenderer.StartAsync<BoardNew>(app.Services, John, "John");
        await ToWhereAsync(app, page, board);
        board.SetOwnRules(null);
        Assert.Contains("STEP 2 OF 3", await page.TextAsync(), StringComparison.Ordinal);

        await page.ClickAsync(Button("Next"));
        var text = await page.TextAsync();
        Assert.Matches($"(?s)Name your relays.*{Regex.Escape(Line)}.*RELAY 1", text);
        Assert.Contains("STEP 3 OF 3", text, StringComparison.Ordinal);

        // Only the rules change: read now, so the line goes; unknown again, so it comes back.
        board.SetOwnRules(OwnRules.Empty);
        await Wait.UntilAsync(() => !page.TextAsync().GetAwaiter().GetResult().Contains(Line, StringComparison.Ordinal));
        board.SetOwnRules(null);
        await Wait.UntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains(Line, StringComparison.Ordinal));

        // Without an answer its rules are only not known.
        board.SetConnected(false);
        await Wait.UntilAsync(() => !page.TextAsync().GetAwaiter().GetResult().Contains(Line, StringComparison.Ordinal));
        Assert.Equal(0, board.OwnRulesTurnOffs);
        Assert.Empty(Errors(page));

        // A board that may also have rules it can't see: "couldn't read" in place of "may also".
        await using (var other = App())
        {
            var shelly = new FakeRelayBoard(ownRules: new OwnRulesAbility(Reads: true, TurnsOff: true, MayHaveUnseen: true));
            using var second = await PageRenderer.StartAsync<BoardNew>(other.Services, John, "John");
            await ToWhereAsync(other, second, shelly);
            shelly.SetOwnRules(null);
            await second.ClickAsync(Button("Next"));
            text = await second.TextAsync();
            Assert.Contains(Line, text, StringComparison.Ordinal);
            Assert.DoesNotContain("may also have rules", text, StringComparison.Ordinal);
        }

        using (TestCulture.Language("sv"))
        {
            Assert.Equal("Contactor kunde inte läsa kortets egna regler. Titta på kortets egen webbsida.", TestTexts.Localizer["OwnRules.Card.CouldntRead"].Value);
        }
    }

    // The step lets go of the board's events when it closes.
    [Fact]
    public async Task Should_LetGoOfTheBoardsEvents_WhenNameYourRelaysCloses()
    {
        await using var app = App();
        var page = await PageRenderer.StartAsync<BoardNew>(app.Services, John, "John");
        await ToWhereAsync(app, page, new FakeRelayBoard(ownRules: OwnRulesAbility.ReadsAndTurnsOff));
        await page.ClickAsync(Button("Next"));
        Assert.Contains("Name your relays", await page.TextAsync(), StringComparison.Ordinal);
        var registry = app.Services.GetRequiredService<BoardRegistry>();
        Assert.Equal(1, PageRenderer.Handlers<BoardRelaysStep>(registry, nameof(registry.OwnRulesChanged)));

        page.Close();

        Assert.Equal(0, PageRenderer.Handlers<BoardRelaysStep>(registry, nameof(registry.OwnRulesChanged)));
        Assert.Equal(0, PageRenderer.Handlers<BoardRelaysStep>(registry, nameof(registry.StateChanged)));
    }

    // Rules turned off at the board while the step shows leave nothing to ask: the page goes on by itself.
    [Fact]
    public async Task Should_GoOn_WhenTheRulesGoAtTheBoard()
    {
        var board = new FakeRelayBoard(ownRules: OwnRulesAbility.ReadsAndTurnsOff);
        await using var app = App();
        using var page = await PageRenderer.StartAsync<BoardNew>(app.Services, John, "John");
        await ToWhereAsync(app, page, board, Timer3, Timer5);
        await page.ClickAsync(Button("Next"));
        Assert.Contains("This board has timers of its own", await page.TextAsync(), StringComparison.Ordinal);

        board.SetOwnRules(OwnRules.Empty);

        await Wait.UntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains("Name your relays", StringComparison.Ordinal));
        Assert.Contains("STEP 4 OF 4", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Equal(0, board.OwnRulesTurnOffs);
        Assert.Empty(Errors(page));
    }

    // Gone on without a connection, the step was skipped (STEP 3 OF 3). The board reports rules later: the relays keep
    // their number, "Back" goes to "Where is the board?" (now STEP 2 OF 4), and "Next" asks about them (STEP 3 OF 4).
    [Fact]
    public async Task Should_GoBackToWhere_AfterGoingOnWithoutAConnection_AndCountTheStepThatComesNow()
    {
        var board = new FakeRelayBoard(connected: false, ownRules: OwnRulesAbility.ReadsAndTurnsOff);
        await using var app = App();
        using var page = await PageRenderer.StartAsync<BoardNew>(app.Services, John, "John");
        var live = await ToWhereAsync(app, page, board);
        await page.ClickAsync(Button("Go on without a connection"));
        Assert.Contains("STEP 3 OF 3", await page.TextAsync(), StringComparison.Ordinal);

        board.SetConnected(true);
        board.SetOwnRules(new OwnRules([Timer3, Timer5]));
        await Wait.UntilAsync(() => app.Services.GetRequiredService<OwnRulesService>().Rows(live.BoardId).Count == 2);
        var text = await page.TextAsync();
        Assert.Contains("Name your relays", text, StringComparison.Ordinal);
        Assert.Contains("STEP 3 OF 3", text, StringComparison.Ordinal);

        await page.ClickAsync(Button("Back"));
        text = await page.TextAsync();
        Assert.Contains("Where is the board?", text, StringComparison.Ordinal);
        Assert.Contains("STEP 2 OF 4", text, StringComparison.Ordinal);

        await page.ClickAsync(Button("Next"));
        text = await page.TextAsync();
        Assert.Contains("This board has timers of its own", text, StringComparison.Ordinal);
        Assert.Contains("STEP 3 OF 4", text, StringComparison.Ordinal);
        Assert.Empty(Errors(page));
    }

    // The app with a first board in memory, signed in as John; the app's clock when given.
    private static TestWebApp App(TimeProvider? time = null)
    {
        return new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John },
            services =>
            {
                TestWebApp.WithBoard(new FakeRelayBoard())(services);
                if (time is not null)
                {
                    services.AddSingleton(time);
                }
            });
    }

    // "Add a board" up to "Where is the board?" for a VM201, run as <paramref name="board"/> with these rules, its
    // address typed as the board's own.
    private static async Task<LiveBoard> ToWhereAsync(TestWebApp app, PageRenderer page, FakeRelayBoard board, params OwnRule[] rules)
    {
        await page.ClickAsync(PageRenderer.With("aria-labelledby", "brand-label brand-value"));
        await page.ClickAsync(PageRenderer.With("value", "Velleman"));
        await page.ClickAsync(PageRenderer.With("value", "vm201"));
        await page.ClickAsync(Button("Next"));
        var registry = app.Services.GetRequiredService<BoardRegistry>();
        var live = registry.All[^1];
        await AtTheBoardsAddressAsync(app, live.BoardId);
        await registry.ReplaceAsync(new FakeBoardParts(board).Build(live.BoardId, Vm201Model.Model), Cancel);
        if (rules.Length > 0 || board.Capabilities.OwnRules is { Reads: true })
        {
            board.SetOwnRules(new OwnRules(rules));
        }

        await page.TypeAsync(PageRenderer.With("id", "host"), "192.168.1.50");
        Assert.Contains("Where is the board?", await page.TextAsync(), StringComparison.Ordinal);
        return live;
    }

    /// <summary>
    /// Saves the board in memory's own address on the board's row before it runs, so the address typed on "Where is the
    /// board?" doesn't move it: the board answers there at once.
    /// </summary>
    internal static async Task AtTheBoardsAddressAsync(TestWebApp app, int boardId)
    {
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel);
        await db.Boards.Where(b => b.Id == boardId).ExecuteUpdateAsync(b => b.SetProperty(x => x.Host, "192.168.1.50").SetProperty(x => x.Port, 9760), Cancel);
    }

    private static async Task<List<EventLogEntry>> EventsAsync(TestWebApp app, int boardId, string action)
    {
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel);
        return await db.EventLog.AsNoTracking().Where(e => e.BoardId == boardId && e.Action == action).OrderBy(e => e.Id).ToListAsync(Cancel);
    }

    private static async Task<List<BoardOwnRule>> RowsAsync(TestWebApp app, int boardId)
    {
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel);
        return await db.BoardOwnRules.AsNoTracking().Where(r => r.BoardId == boardId).OrderBy(r => r.Channel).ToListAsync(Cancel);
    }

    // What went wrong on the page, but for moving the focus to a new step's heading, which needs a browser.
    private static List<Exception> Errors(PageRenderer page)
    {
        return [.. page.Errors.Where(e => !e.ToString().Contains("ElementReference has not been configured correctly", StringComparison.Ordinal))];
    }

    // A button by its words: only buttons have a type.
    private static Func<IReadOnlyDictionary<string, object?>, bool> Button(string text)
    {
        return attributes => attributes.ContainsKey("type") && PageRenderer.WithText(text)(attributes);
    }

    private static Task<bool> DisabledAsync(PageRenderer page, Func<IReadOnlyDictionary<string, object?>, bool> button)
    {
        return page.HasAsync(attributes => button(attributes) && attributes.GetValueOrDefault("disabled") is true);
    }
}
