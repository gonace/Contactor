using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Contactor.Boards;
using Contactor.Tests.Boards;
using Contactor.Web.Components;
using Contactor.Web.Features.BoardRules;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Contactor.Tests.Web;

/// <summary>
/// A relay its board controls by itself and ignores commands for (<see cref="OwnRuleKind.InputLinkage"/>, e.g. a
/// Waveshare relay in linkage mode, issue #497): the app never pretends to switch it. Nothing sends it a command (a
/// person, "All off", a schedule, auto-off, a group), nothing logs that it did, and nothing loops on it; it can't join a
/// group, and a group with one doesn't start. A person resets it, which is logged.
/// </summary>
public sealed partial class UnavailableRelaysTests
{
    private const string John = "john@example.com";
    private const int BoardId = 1;

    private static readonly OwnRule Linkage3 = new(3, OwnRuleKind.InputLinkage);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ----- The single place commands are refused, and the services that switch relays on their own -----

    [Fact]
    public async Task Should_RefuseToSwitchIt_ForAPerson_AndLogNothing()
    {
        await using var rig = await BoardTestRig.StartAsync(secondBoard: true);
        var board = rig.Second!;
        var sent = Count(board);
        board.SetOwnRules(new OwnRules([Linkage3]));

        await Assert.ThrowsAsync<RelayUnavailableException>(() => rig.Control.TurnOnAsync(rig.SecondRelayId(3), John, Cancel));
        await Assert.ThrowsAsync<RelayUnavailableException>(() => rig.Control.TurnOffAsync(rig.SecondRelayId(3), John, Cancel));

        Assert.Equal(0, sent.Value);
        Assert.DoesNotContain(await rig.EventsAsync(), e => e.RelayId == rig.SecondRelayId(3));

        // The others on the board work as ever.
        await rig.Control.TurnOnAsync(rig.SecondRelayId(2), John, Cancel);
        Assert.True(board.State.IsOn(2));
    }

    [Fact]
    public async Task Should_LeaveItOut_OfAllOff_WithoutAnEvent()
    {
        await using var rig = await BoardTestRig.StartAsync(secondBoard: true);
        var board = rig.Second!;
        await rig.Control.TurnOnAsync(rig.SecondRelayId(1), John, Cancel);
        await rig.Control.TurnOnAsync(rig.SecondRelayId(3), John, Cancel);
        board.SetOwnRules(new OwnRules([Linkage3]));

        await rig.Control.TurnAllOffAsync(rig.SecondBoardId, John, Cancel);

        // The fake board would have turned it off had an OFF been sent for it: none was.
        Assert.Equal(ChannelMask.Of(3), board.State.Relays);
        var offs = (await rig.EventsAsync()).Where(e => e.Action == EventActions.Off).Select(e => e.RelayId).ToList();
        Assert.Equal([rig.SecondRelayId(1)], offs);
    }

    [Fact]
    public async Task Should_NotTurnItOff_OnAutoOff_NorSpam_UntilItIsReset()
    {
        var errors = new ErrorCollector();
        await using var rig = await BoardTestRig.StartAsync(logs: errors, secondBoard: true);
        var board = rig.Second!;
        var relayId = rig.SecondRelayId(3);
        await rig.Control.TurnOnAsync(relayId, John, Cancel);
        board.SetOwnRules(new OwnRules([Linkage3]));
        var sent = Count(board);
        rig.Time.Now += TimeSpan.FromMinutes(Relay.DefaultMaxOnMinutes + 1);
        var autoOff = rig.Database.Services.GetRequiredService<AutoOffService>();

        // Check after check: nothing sent, nothing logged, and the run with its auto-off stays.
        for (var check = 0; check < 5; check++)
        {
            Assert.Empty(await autoOff.CheckAsync(Cancel));
            rig.Time.Now += TimeSpan.FromSeconds(15);
        }

        Assert.Equal(0, sent.Value);
        Assert.True(board.State.IsOn(3));
        Assert.DoesNotContain(await rig.EventsAsync(), e => e.Actor == EventActors.AutoOff);
        Assert.Empty(errors.Lines);

        // Reset: the auto-off that is due turns it off at the next check.
        board.SetOwnRules(OwnRules.Empty);
        Assert.Equal([relayId], await autoOff.CheckAsync(Cancel));
        Assert.False(board.State.IsOn(3));
    }

    [Fact]
    public async Task Should_NotTurnItOn_FromASchedule_NorSpam_AndStartItOnceReset()
    {
        var errors = new ErrorCollector();
        await using var rig = await BoardTestRig.StartAsync(logs: errors, secondBoard: true);
        var board = rig.Second!;
        var relayId = rig.SecondRelayId(3);
        board.SetOwnRules(new OwnRules([Linkage3]));
        await AddScheduleAsync(rig, relayId, groupId: null);
        await rig.Scheduler.TickAsync(Cancel);
        var sent = Count(board);

        for (var minute = 5; minute <= 9; minute++)
        {
            await TickAtAsync(rig, 14, minute);
        }

        Assert.Equal(0, sent.Value);
        Assert.False(board.State.IsOn(3));
        Assert.DoesNotContain(await rig.EventsAsync(), e => e.RelayId == relayId);
        Assert.Empty(errors.Lines);

        // Reset during its window: the schedule turns it on at the next tick.
        board.SetOwnRules(OwnRules.Empty);
        await TickAtAsync(rig, 14, 10);
        Assert.True(board.State.IsOn(3));
        Assert.Contains(await rig.EventsAsync(), e => e.RelayId == relayId && e.Action == EventActions.On && e.Actor == EventActors.Schedule);
    }

    // ----- Groups -----

    [Fact]
    public async Task Should_NotLetItJoinAGroup()
    {
        await using var rig = await BoardTestRig.StartAsync(secondBoard: true);
        rig.Second!.SetOwnRules(new OwnRules([Linkage3]));
        var relayId = rig.SecondRelayId(3);

        var candidate = Assert.Single(await rig.GroupSetup.CandidatesAsync(Cancel), c => c.RelayId == relayId);
        Assert.True(candidate.Unavailable);
        Assert.False(candidate.IsFree);
        Assert.Equal("Unavailable to Contactor", TestTexts.GroupPlan.Note(candidate));
        Assert.DoesNotContain(await rig.GroupSetup.FreeRelaysAsync(rig.SecondBoardId, Cancel), r => r.Id == relayId);

        var (id, error) = await rig.GroupSetup.CreateAsync(Draft(rig, 1, 3), John, Cancel);
        Assert.Null(id);
        Assert.Equal("Relay 3 can't join a group: the board controls it itself.", error);
    }

    [Fact]
    public async Task Should_RefuseToStartAGroupWithIt_ButStillStopIt_AndKeepItsOtherChanges()
    {
        var errors = new ErrorCollector();
        await using var rig = await BoardTestRig.StartAsync(logs: errors, secondBoard: true);
        var board = rig.Second!;
        var (id, error) = await rig.GroupSetup.CreateAsync(Draft(rig, 1, 2), John, Cancel);
        Assert.Null(error);
        await rig.Control.TurnOnAsync(rig.SecondRelayId(4), John, Cancel);
        var fanOn = Count(board);

        // Heat became linkage: the group doesn't start, and nothing is sent or logged for it.
        board.SetOwnRules(new OwnRules([new OwnRule(2, OwnRuleKind.InputLinkage)]));
        await Assert.ThrowsAsync<GroupUnavailableException>(() => rig.Groups.StartGroupAsync(id!.Value, John, Cancel));
        Assert.Equal("Heat is unavailable to Contactor: the board controls it itself.", await rig.Groups.SetMemberAsync(id!.Value, rig.SecondRelayId(2), on: true, John, Cancel));
        Assert.Equal(0, fanOn.Value);
        Assert.DoesNotContain(await rig.EventsAsync(), e => e.GroupId == id && e.Action == EventActions.GroupOn);

        // Its other changes still save, e.g. a new name: Heat stays a member.
        Assert.Null(await rig.GroupSetup.SaveAsync(id.Value, Draft(rig, 1, 2) with { Name = "Barn heater" }, John, Cancel));

        // A group schedule that starts it does nothing, quietly, tick after tick.
        await AddScheduleAsync(rig, relayId: null, groupId: id);
        await rig.Scheduler.TickAsync(Cancel);
        for (var minute = 5; minute <= 8; minute++)
        {
            await TickAtAsync(rig, 14, minute);
        }

        Assert.Equal(0, fanOn.Value);
        Assert.Empty(errors.Lines);

        // Stopping still works as far as the board allows: Fan goes off.
        board.SetRelay(1, on: true);
        await rig.Groups.StopGroupAsync(id.Value, John, Cancel);
        await rig.Groups.StepAsync(Cancel);
        Assert.False(board.State.IsOn(1));
    }

    // ----- The screens -----

    [Fact]
    public async Task Should_ShowIt_UnderUnavailable_WithoutOnOff_AndOnlyOrdinaryRulesInTheWarning()
    {
        var board = Board();
        await using var app = App(board);
        await RulesAsync(app, board, Linkage3, new OwnRule(5, OwnRuleKind.InputToggle));

        var html = await PageAsync(app, "/");

        Assert.Contains("Unavailable to Contactor", html, StringComparison.Ordinal);
        var card = Card(html, "relay/3");
        Assert.Contains("The board controls this relay itself: it follows input 3. Reset it to control it from here.", Text(card), StringComparison.Ordinal);
        Assert.Contains(">Reset</button>", card, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-pressed", card, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(html, "href=\"relay/3\""));

        // After the relays with an ON/OFF, before the inputs.
        Assert.True(html.IndexOf("href=\"relay/8\"", StringComparison.Ordinal) < html.IndexOf("Unavailable to Contactor", StringComparison.Ordinal));
        Assert.True(html.IndexOf("Unavailable to Contactor", StringComparison.Ordinal) < html.IndexOf("href=\"input/", StringComparison.Ordinal));

        // The warning has only the toggle, not the relay shown as unavailable.
        Assert.Contains("The board has an input that flips a relay", html, StringComparison.Ordinal);
        Assert.Contains("It can switch relay 5 without Contactor.", html, StringComparison.Ordinal);
        Assert.Contains("A pulse on its input can flip it", Text(Card(html, "relay/5")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ResetIt_WhenThePersonPresses_AndLogIt()
    {
        var board = Board();
        await using var app = App(board);
        await RulesAsync(app, board, Linkage3);
        using var page = await PageRenderer.StartAsync<Relays>(app.Services, John, "John");

        await page.ClickAsync(PageRenderer.WithText("Reset"));

        Assert.Equal(OwnRules.Empty, board.State.OwnRules);
        await Wait.UntilAsync(() => !page.TextAsync().GetAwaiter().GetResult().Contains("Unavailable to Contactor", StringComparison.Ordinal));
        await using var db = await Factory(app).CreateDbContextAsync(Cancel);
        var entry = await db.EventLog.SingleAsync(e => e.Action == EventActions.BoardOwnRulesTurnedOff, Cancel);
        Assert.Equal((John, BoardId, """{"relays":[3],"kinds":["input-linkage"]}"""), (entry.Actor, entry.BoardId, entry.Details));
        Assert.Equal("{0} reset relay 3", TestTexts.Events.For(entry, null, "John").Line);
        Assert.Empty(page.Errors);
    }

    [Fact]
    public async Task Should_WarnOnTheGroup_AndRefuseItsStart_UntilReset()
    {
        var board = Board();
        await using var app = App(board);
        var handler = app.Services.GetRequiredService<GroupsHandler>();
        var (id, error) = await handler.CreateAsync(
            new GroupDraft("Fan heater", GroupIcons.Fan, 120, [new GroupStepDraft("Fan", [1]), new GroupStepDraft("Heat", [2])]),
            John,
            Cancel);
        Assert.Null(error);
        await RulesAsync(app, board, new OwnRule(2, OwnRuleKind.InputLinkage));

        var relays = await PageAsync(app, "/");
        var card = Card(relays, $"group/{id}");
        Assert.Contains("Heat is unavailable to Contactor", Text(card), StringComparison.Ordinal);
        Assert.Contains("The board controls it itself. Reset it to start the group.", Text(card), StringComparison.Ordinal);
        Assert.Matches("class=\"toggle\"[^>]*disabled", card);

        var group = await PageAsync(app, $"/group/{id}");
        Assert.Contains("class=\"offline group-unavailable\"", group, StringComparison.Ordinal);
        Assert.Contains("Heat is unavailable to Contactor", group, StringComparison.Ordinal);
        Assert.Matches("class=\"toggle\"[^>]*disabled", group);

        using var page = await PageRenderer.StartAsync<GroupDetail>(app.Services, John, "John", new Dictionary<string, object?> { [nameof(GroupDetail.Id)] = id!.Value });
        await page.ClickAsync(PageRenderer.WithText("Reset"));
        Assert.Equal(OwnRules.Empty, board.State.OwnRules);
        await Wait.UntilAsync(() => !page.TextAsync().GetAwaiter().GetResult().Contains("unavailable", StringComparison.Ordinal));
        Assert.Empty(page.Errors);
    }

    [Fact]
    public async Task Should_OfferNoKeep_ForALinkageRule_ButForToggleAndEdge()
    {
        var board = Board();
        await using var app = App(board);
        await RulesAsync(app, board, Linkage3, new OwnRule(5, OwnRuleKind.InputToggle), new OwnRule(6, OwnRuleKind.InputEdge));
        var rules = app.Services.GetRequiredService<OwnRulesHandler>();

        var html = await PageAsync(app, $"/board/{BoardId}");
        var start = html.IndexOf("own-rules-card", StringComparison.Ordinal);
        var cardHtml = html[start..html.IndexOf("</section>", start, StringComparison.Ordinal)];
        Assert.Equal(2, Regex.Count(cardHtml, ">Keep</button>"));
        Assert.Equal(1, Regex.Count(cardHtml, ">Reset</button>"));
        Assert.Equal(2, Regex.Count(cardHtml, ">Turn off</button>"));

        Assert.False(OwnRulesHandler.CanKeep(OwnRuleKind.InputLinkage));
        Assert.True(OwnRulesHandler.CanKeep(OwnRuleKind.InputToggle) && OwnRulesHandler.CanKeep(OwnRuleKind.InputEdge));
        Assert.Equal("The board controls this relay itself, so it can't be kept. Reset it instead.", await rules.KeepAsync(BoardId, 3, OwnRuleKind.InputLinkage, John, Cancel));
        Assert.Null(await rules.KeepAsync(BoardId, 5, OwnRuleKind.InputToggle, John, Cancel));

        // "Keep them" while adding a board keeps the toggle and edge only; the linkage stays a warning.
        Assert.Null(await rules.KeepAllAsync(BoardId, [Linkage3, new OwnRule(6, OwnRuleKind.InputEdge)], John, Cancel));
        Assert.Equal([Linkage3], rules.Warnings(BoardId));
    }

    [Fact]
    public void Should_SayTheInputKinds_InEnglishAndSwedish()
    {
        var toggle = new OwnRule(5, OwnRuleKind.InputToggle);
        var edge = new OwnRule(6, OwnRuleKind.InputEdge);
        Assert.Equal("The board controls a relay itself", TestTexts.OwnRules.Heading([Linkage3], null));
        Assert.Equal("Garage has inputs that flip relays at each change", TestTexts.OwnRules.Heading([edge, new OwnRule(7, OwnRuleKind.InputEdge)], "Garage"));
        Assert.Equal("The board has input rules of its own", TestTexts.OwnRules.Heading([toggle, edge], null));
        Assert.Equal("This board has an input that flips a relay", TestTexts.OwnRules.StepTitle([toggle]));
        Assert.Equal("Each change of an input can flip Heat", TestTexts.OwnRules.GroupLine([OwnRuleKind.InputEdge], ["Heat"]));
        Assert.Equal("The board's input rules", TestTexts.OwnRules.CardTitle([OwnRuleKind.InputToggle, OwnRuleKind.InputEdge]));
        Assert.Equal("Fan and Heat are unavailable to Contactor", TestTexts.OwnRules.GroupUnavailable(["Fan", "Heat", "Heat"]));

        using var swedish = TestCulture.Language("sv");
        Assert.Equal("Kortet styr reläet självt: det följer ingång 3. Återställ det för att styra det härifrån.", TestTexts.OwnRules.Unavailable(3));
        Assert.Equal("Värme är inte tillgängligt för Contactor", TestTexts.OwnRules.GroupUnavailable(["Värme"]));
        Assert.Equal("Kortet har en ingång som slår om ett relä", TestTexts.OwnRules.StepTitle([toggle]));
        Assert.Equal("Inte tillgängliga för Contactor", TestTexts.Localizer["OwnRules.Unavailable.Heading"].Value);
        Assert.Equal("Återställ", TestTexts.Localizer["OwnRules.Reset"].Value);
        Assert.Equal("Kortet styr det självt. Återställ det för att starta gruppen.", TestTexts.Localizer["OwnRules.Unavailable.GroupText_One"].Value);
        var reset = new EventLogEntry { Actor = John, BoardId = BoardId, Action = EventActions.BoardOwnRulesTurnedOff, Details = """{"relays":[3,5],"kinds":["input-linkage"]}""" };
        Assert.Equal("{0} återställde relä 3 och 5", TestTexts.Events.For(reset, null, "John").Line);
    }

    // ----- Helpers -----

    private static GroupDraft Draft(BoardTestRig rig, int fan, int heat)
    {
        return new GroupDraft("Fan heater", GroupIcons.Fan, 120, [new GroupStepDraft("Fan", [rig.SecondRelayId(fan)]), new GroupStepDraft("Heat", [rig.SecondRelayId(heat)])]);
    }

    // Every command that reaches the fake board, ON or OFF.
    private static StrongBoxCount Count(FakeRelayBoard board)
    {
        var count = new StrongBoxCount();
        board.BeforeOn = () =>
        {
            Interlocked.Increment(ref count.Value);
            return Task.CompletedTask;
        };
        board.BeforeOff = () =>
        {
            Interlocked.Increment(ref count.Value);
            return Task.CompletedTask;
        };
        return count;
    }

    // 14:00 local is 13:00 UTC on the rig's day.
    private static async Task TickAtAsync(BoardTestRig rig, int hour, int minute)
    {
        rig.Time.Now = new DateTimeOffset(2026, 11, 4, hour - 1, minute, 0, TimeSpan.Zero);
        await rig.Scheduler.TickAsync(Cancel);
    }

    // Every day from 14:05 to 18:00 local, for a relay or a group.
    private static async Task AddScheduleAsync(BoardTestRig rig, int? relayId, int? groupId)
    {
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        db.Schedules.Add(new Schedule
        {
            RelayId = relayId,
            GroupId = groupId,
            Days = ScheduleDays.All,
            OnTime = new TimeOnly(14, 5),
            OffTime = new TimeOnly(18, 0),
            IsEnabled = true,
            CreatedBy = John,
        });
        await db.SaveChangesAsync(Cancel);
    }

    private static FakeRelayBoard Board()
    {
        return new FakeRelayBoard(ownRules: OwnRulesAbility.ReadsAndTurnsOff);
    }

    private static TestWebApp App(FakeRelayBoard board)
    {
        return new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John },
            TestWebApp.WithBoard(board));
    }

    private static IDbContextFactory<ContactorDbContext> Factory(TestWebApp app)
    {
        return app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
    }

    // The board reports the rules, and the app has read them.
    private static async Task RulesAsync(TestWebApp app, FakeRelayBoard board, params OwnRule[] rules)
    {
        board.SetOwnRules(new OwnRules(rules));
        await Wait.UntilAsync(() => app.Services.GetRequiredService<OwnRulesService>().Rows(BoardId).Count == rules.Length);
    }

    private static async Task<string> PageAsync(TestWebApp app, string path)
    {
        using var http = app.CreateClient();
        return WebUtility.HtmlDecode(await http.GetStringAsync(path, Cancel));
    }

    // A card, from its link to the next card.
    private static string Card(string html, string href)
    {
        var at = html.IndexOf($"href=\"{href}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"No card for {href}.");
        var start = html.LastIndexOf("class=\"card", at, StringComparison.Ordinal);
        var end = html.IndexOf("class=\"card", at, StringComparison.Ordinal);
        return html[start..(end < 0 ? html.Length : end)];
    }

    private static string Text(string html)
    {
        return Regex.Replace(Tags().Replace(html, ""), @"\s+", " ");
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    private sealed class StrongBoxCount
    {
        public int Value;
    }

    // The error lines logged, by their message.
    private sealed class ErrorCollector : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines;

        public ILogger CreateLogger(string categoryName)
        {
            return this;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= LogLevel.Error;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                _lines.Enqueue(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }
    }
}
