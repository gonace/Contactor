using System.Net;
using Contactor.Boards;
using Contactor.Tests.Boards;
using Contactor.Web.Components;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Languages;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// A group's relays after a power cut (Issue #516): every relay in a group is Off on the board (<see cref="GroupPowerOn"/>).
/// The group's card and page warn when the board has another state, with "Make it safe"; "New group" and adding a relay
/// on the group's page set the group's relays to Off after the group is saved.
/// </summary>
public sealed class GroupPowerOnTests
{
    private const string John = "john@example.com";

    private static readonly PowerOnAbility AllStates = new([PowerOnState.Off, PowerOnState.AsBefore, PowerOnState.On, PowerOnState.FollowsInput], perRelay: true);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // The rule: only Off is allowed.
    [Theory]
    [InlineData(PowerOnState.Off, true)]
    [InlineData(PowerOnState.AsBefore, false)]
    [InlineData(PowerOnState.On, false)]
    [InlineData(PowerOnState.FollowsInput, false)]
    public void Should_AllowOnlyOff(PowerOnState state, bool allowed)
    {
        Assert.Equal(allowed, GroupPowerOn.IsAllowed(state));
    }

    // Every state other than Off that the board reported, in the order given and each once; never an unknown state.
    [Fact]
    public void Should_FindTheRelaysNotOff_InTheOrderGiven()
    {
        var states = new PowerOnStates([PowerOnState.On, PowerOnState.Off, PowerOnState.AsBefore, null, PowerOnState.FollowsInput, PowerOnState.Off]);

        Assert.Equal([5, 3, 1], GroupPowerOn.NotSafe(AllStates, states, [5, 2, 3, 3, 4, 1, 6]));
        Assert.Empty(GroupPowerOn.NotSafe(AllStates, states, [2, 4, 6]));
        Assert.Empty(GroupPowerOn.NotSafe(AllStates, states, [7, 8]));
        Assert.Empty(GroupPowerOn.NotSafe(AllStates, states, []));
    }

    // A board that can't tell, or hasn't reported its states yet, breaks nothing.
    [Fact]
    public void Should_FindNone_WithoutTheAbility_OrBeforeTheBoardReported()
    {
        var states = new PowerOnStates([PowerOnState.On, PowerOnState.On]);

        Assert.Empty(GroupPowerOn.NotSafe(null, states, [1, 2]));
        Assert.Empty(GroupPowerOn.NotSafe(AllStates, null, [1, 2]));
    }

    // Each relay on a board with a state per relay; one write on a board with one state for every relay.
    [Fact]
    public void Should_WriteEachRelay_OrOnceForTheWholeBoard()
    {
        var wholeBoard = new PowerOnAbility(AllStates.States, perRelay: false);

        Assert.Equal([3, 5], GroupPowerOn.Writes(AllStates, [3, 5, 3]));
        Assert.Equal([3], GroupPowerOn.Writes(wholeBoard, [3, 5]));
        Assert.Empty(GroupPowerOn.Writes(wholeBoard, []));
    }

    // Unknown: a board with the ability that didn't report a relay's state, or no state at all. A board without the
    // ability can't tell, so nothing is unknown there.
    [Fact]
    public void Should_FindTheUnknownRelays_AndSetThemOffOnSave_WithTheRelaysNotOff()
    {
        var states = new PowerOnStates([PowerOnState.On, null, PowerOnState.Off, null]);

        Assert.Equal([4, 2], GroupPowerOn.Unknown(AllStates, states, [4, 1, 2, 3, 2]));
        Assert.Equal([1, 2, 3], GroupPowerOn.Unknown(AllStates, null, [1, 2, 3]));
        Assert.Empty(GroupPowerOn.Unknown(null, states, [2, 4]));
        Assert.Equal([4, 1, 2], GroupPowerOn.ToSetOnSave(AllStates, states, [4, 3, 1, 2, 4]));
        Assert.Equal([1, 2], GroupPowerOn.ToSetOnSave(AllStates, null, [1, 2]));
        Assert.Empty(GroupPowerOn.ToSetOnSave(null, states, [1, 2]));
    }

    [Fact]
    public async Task Should_WarnOnTheGroupsCardAndPage_NamingTheRelaysNotOff()
    {
        var board = Board(heat: PowerOnState.On);
        await using var app = App(board);
        var groupId = await AddGroupAsync(app);
        using var http = app.CreateClient();

        var card = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));
        var page = WebUtility.HtmlDecode(await http.GetStringAsync($"/group/{groupId}", Cancel));

        foreach (var html in new[] { card, page })
        {
            Assert.Contains("After a power cut, the board can turn Heat on by itself, without the group's start order.", html, StringComparison.Ordinal);
            Assert.Contains("\"Make it safe\" sets it to Off on the board. Contactor then starts the group in its order.", html, StringComparison.Ordinal);
            Assert.Contains(">Make it safe</button>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("every relay on the board", html, StringComparison.Ordinal);
        }

        // The group can still start: the risk is at the power cut, not at the start.
        Assert.Matches("<button type=\"button\" class=\"toggle\" data-vibrate aria-pressed=\"false\"(?![^>]*disabled)", page);
        Assert.DoesNotContain("Can't see what the board does after a power cut.", page, StringComparison.Ordinal);
        Assert.Empty(board.PowerOnWrites);
    }

    // As before, Follows its switch and On all break the rule; two relays are named together.
    [Theory]
    [InlineData(PowerOnState.AsBefore)]
    [InlineData(PowerOnState.FollowsInput)]
    public async Task Should_WarnForEveryStateButOff(PowerOnState state)
    {
        var board = Board(heat: state, fan: PowerOnState.On);
        await using var app = App(board);
        var groupId = await AddGroupAsync(app);
        using var http = app.CreateClient();

        var page = WebUtility.HtmlDecode(await http.GetStringAsync($"/group/{groupId}", Cancel));

        Assert.Contains("After a power cut, the board can turn Fan and Heat on by itself, without the group's start order.", page, StringComparison.Ordinal);
        Assert.Contains("\"Make it safe\" sets them to Off on the board.", page, StringComparison.Ordinal);
    }

    // All Off, unknown, or a board that can't tell: no warning. Only the board that can't tell gets the quiet note, and
    // only on the group's page.
    [Theory]
    [InlineData("off")]
    [InlineData("unknown")]
    [InlineData("not reported")]
    [InlineData("no ability")]
    public async Task Should_NotWarn_WhenNothingIsKnownToTurnOn(string what)
    {
        var board = what switch
        {
            "off" => Board(heat: PowerOnState.Off),
            "unknown" => Board(heat: null),
            "not reported" => new FakeRelayBoard(powerOn: AllStates),
            _ => new FakeRelayBoard(),
        };
        await using var app = App(board);
        var groupId = await AddGroupAsync(app);
        using var http = app.CreateClient();

        var card = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));
        var page = WebUtility.HtmlDecode(await http.GetStringAsync($"/group/{groupId}", Cancel));

        Assert.DoesNotContain("Make it safe", card, StringComparison.Ordinal);
        Assert.DoesNotContain("Make it safe", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Can't see what the board does", card, StringComparison.Ordinal);
        Assert.Equal(what == "no ability", page.Contains("Can't see what the board does after a power cut. Check it on the board.", StringComparison.Ordinal));
        Assert.Equal(what is "unknown" or "not reported", page.Contains("Can't see yet what the board does after a power cut.", StringComparison.Ordinal));
        Assert.DoesNotContain("Can't see yet", card, StringComparison.Ordinal);
        Assert.Empty(board.PowerOnWrites);
    }

    // "Make it safe" on the group's page writes Off for the group's relays that aren't Off, and only on the press.
    [Fact]
    public async Task Should_SetOnlyTheGroupsRelaysNotOff_ToOff_OnThePress_AndLogIt()
    {
        var board = Board(heat: PowerOnState.On, relay5: PowerOnState.On);
        await using var app = App(board);
        var groupId = await AddGroupAsync(app);
        using var page = await OpenGroupAsync(app, groupId);
        Assert.Empty(board.PowerOnWrites);

        await page.ClickAsync(PageRenderer.WithText("Make it safe"));

        Assert.Equal([(3, PowerOnState.Off)], board.PowerOnWrites);
        Assert.Equal(PowerOnState.On, board.State.PowerOn?.For(5));
        var logged = Assert.Single(await PowerOnEventsAsync(app));
        Assert.Equal((John, 3, 1, """{"from":"on","to":"off"}"""), (logged.Actor, logged.RelayId, logged.BoardId, logged.Details));
        await Wait.UntilAsync(() => !page.HasAsync(PageRenderer.WithText("Make it safe")).GetAwaiter().GetResult());
        Assert.Empty(page.Errors);
    }

    // The card's "Make it safe" does the same.
    [Fact]
    public async Task Should_MakeItSafe_FromTheGroupsCard()
    {
        var board = Board(heat: PowerOnState.On);
        await using var app = App(board);
        await AddGroupAsync(app);
        using var page = await PageRenderer.StartAsync<Relays>(app.Services, John, "John");

        await page.ClickAsync(PageRenderer.WithText("Make it safe"));

        Assert.Equal([(3, PowerOnState.Off)], board.PowerOnWrites);
        Assert.Single(await PowerOnEventsAsync(app));
        await Wait.UntilAsync(() => !page.HasAsync(PageRenderer.WithText("Make it safe")).GetAwaiter().GetResult());
        Assert.Empty(page.Errors);
    }

    // Without contact the button is disabled, with the reason, and nothing is sent or queued, also when pressed.
    [Fact]
    public async Task Should_DisableMakeItSafe_WithoutContact_AndSendNothing()
    {
        var board = Board(heat: PowerOnState.On);
        await using var app = App(board);
        var groupId = await AddGroupAsync(app);
        board.SetConnected(false);
        using var http = app.CreateClient();

        var card = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));
        var html = WebUtility.HtmlDecode(await http.GetStringAsync($"/group/{groupId}", Cancel));
        var error = await app.Services.GetRequiredService<RelayPowerOnHandler>().MakeGroupSafeAsync(groupId, John, relayIds: null, onSave: false, Cancel);

        foreach (var shown in new[] { card, html })
        {
            Assert.Matches("class=\"[^\"]*make-safe\" disabled", shown);
            Assert.Contains("No contact with the board, so this can't be changed now.", shown, StringComparison.Ordinal);
        }

        Assert.Equal("No contact with the board, so nothing was changed. Try again when it answers.", error);
        board.SetConnected(true);
        Assert.Empty(board.PowerOnWrites);
        Assert.Empty(await PowerOnEventsAsync(app));
    }

    // A write the board didn't take says so under the warning, and logs nothing.
    [Fact]
    public async Task Should_SaySo_WhenTheBoardDidntTakeIt()
    {
        var board = Board(heat: PowerOnState.On);
        board.RefusePowerOn = true;
        await using var app = App(board);
        var groupId = await AddGroupAsync(app);
        using var page = await OpenGroupAsync(app, groupId);

        await page.ClickAsync(PageRenderer.WithText("Make it safe"));

        Assert.Contains("Contactor couldn't confirm the change. Check it here once the board answers.", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(await PowerOnEventsAsync(app));
    }

    // One state for every relay: the same rule. The warning names the group's relays and says it is for the whole board;
    // one write sets the board to Off.
    [Fact]
    public async Task Should_SetTheWholeBoardToOff_WithOneWrite()
    {
        var board = new FakeRelayBoard(powerOn: new PowerOnAbility(AllStates.States, perRelay: false));
        board.SetPowerOn(new PowerOnStates(Enumerable.Repeat<PowerOnState?>(PowerOnState.AsBefore, 8)));
        await using var app = App(board);
        var groupId = await AddGroupAsync(app);
        using var page = await OpenGroupAsync(app, groupId);
        var text = await page.TextAsync();
        Assert.Contains("the board can turn Fan and Heat on by itself", text, StringComparison.Ordinal);
        Assert.Contains("This applies to every relay on the board.", text, StringComparison.Ordinal);

        await page.ClickAsync(PageRenderer.WithText("Make it safe"));

        Assert.Equal([(2, PowerOnState.Off)], board.PowerOnWrites);
        Assert.All(board.State.PowerOn!, s => Assert.Equal(PowerOnState.Off, s));
        Assert.Equal(2, Assert.Single(await PowerOnEventsAsync(app)).RelayId);
    }

    // The review says it, and "Create group" writes Off after the group is saved.
    [Fact]
    public async Task Should_SayItInNewGroup_AndSetTheRelaysToOff_OnCreate()
    {
        var board = Board(heat: PowerOnState.On);
        await using var app = App(board);
        using var page = await AtReviewAsync(app, 2, 3);
        var review = await page.TextAsync();
        Assert.Contains("AFTER A POWER CUT", review, StringComparison.Ordinal);
        Assert.Contains("Contactor sets the board to keep Relay 3 off after a power cut. Contactor then starts the group in its order.", review, StringComparison.Ordinal);
        Assert.Empty(board.PowerOnWrites);

        await page.ClickAsync(Button("Create group"));

        Assert.IsType<NavigationException>(Assert.Single(page.Errors));
        Assert.Equal([(3, PowerOnState.Off)], board.PowerOnWrites);
        Assert.Equal(3, Assert.Single(await PowerOnEventsAsync(app)).RelayId);
    }

    // Nothing said or written when every relay is Off already, or for a board that can't tell.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_SayNothingInNewGroup_WhenThereIsNothingToSet(bool hasAbility)
    {
        var board = hasAbility ? Board(heat: PowerOnState.Off) : new FakeRelayBoard();
        await using var app = App(board);
        using var page = await AtReviewAsync(app, 2, 3);
        Assert.DoesNotContain("AFTER A POWER CUT", await page.TextAsync(), StringComparison.Ordinal);

        await page.ClickAsync(Button("Create group"));

        Assert.IsType<NavigationException>(Assert.Single(page.Errors));
        Assert.Empty(board.PowerOnWrites);
    }

    // A write that fails leaves the group saved: the page says so where the person pressed, and links to the group,
    // whose warning has "Make it safe". A second "Create group" can't make another group.
    [Fact]
    public async Task Should_KeepTheNewGroup_AndSaySo_WhenTheWriteFails()
    {
        var board = Board(heat: PowerOnState.On);
        board.RefusePowerOn = true;
        await using var app = App(board);
        using var page = await AtReviewAsync(app, 2, 3);

        await page.ClickAsync(Button("Create group"));

        Assert.Empty(page.Errors);
        var text = await page.TextAsync();
        Assert.Contains("The group was created, but Contactor couldn't make sure that the board keeps its relays off after a power cut. Open the group to see what to do.", text, StringComparison.Ordinal);
        var groupId = await SingleGroupIdAsync(app);
        Assert.True(await page.HasAsync(PageRenderer.With("href", $"group/{groupId}")));
        Assert.False(await page.HasAsync(Button("Create group")));
        using var http = app.CreateClient();
        Assert.Contains(">Make it safe</button>", WebUtility.HtmlDecode(await http.GetStringAsync($"/group/{groupId}", Cancel)), StringComparison.Ordinal);
    }

    // A write that fails for a state the board hasn't reported: the group's page has no warning yet, only the quiet
    // note, so the page doesn't send the person there for something to press.
    [Fact]
    public async Task Should_SayTheGroupWarnsLater_WhenTheWriteFailsForAnUnknownState()
    {
        var board = Board(heat: null);
        board.RefusePowerOn = true;
        await using var app = App(board);
        using var page = await AtReviewAsync(app, 2, 3);

        await page.ClickAsync(Button("Create group"));

        Assert.Empty(page.Errors);
        var text = await page.TextAsync();
        Assert.Contains("The group was created, but Contactor couldn't make sure that the board keeps its relays off after a power cut. The board hasn't reported yet what they do. If it reports that it turns one on, the group's page warns.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Open the group to see what to do.", text, StringComparison.Ordinal);
        var groupId = await SingleGroupIdAsync(app);
        Assert.True(await page.HasAsync(PageRenderer.With("href", $"group/{groupId}")));
        using var http = app.CreateClient();
        var group = WebUtility.HtmlDecode(await http.GetStringAsync($"/group/{groupId}", Cancel));
        Assert.DoesNotContain(">Make it safe</button>", group, StringComparison.Ordinal);
        Assert.Contains("Can't see yet what the board does after a power cut.", group, StringComparison.Ordinal);
    }

    // Adding a relay on the group's page: the page says it, and Save writes Off for the added relay only.
    [Fact]
    public async Task Should_SetAnAddedRelayToOff_OnSave()
    {
        var board = Board(heat: PowerOnState.Off, relay5: PowerOnState.On);
        board.SetPowerOn(new PowerOnStates([PowerOnState.On, PowerOnState.Off, PowerOnState.Off, PowerOnState.Off, PowerOnState.On, PowerOnState.Off, PowerOnState.Off, PowerOnState.Off]));
        await using var app = App(board);
        var groupId = await AddGroupAsync(app);
        using var page = await OpenGroupAsync(app, groupId);
        Assert.DoesNotContain("When you save", await page.TextAsync(), StringComparison.Ordinal);

        await AddStepAsync(page, relayId: 5);
        Assert.Contains("When you save, Contactor sets the board to keep Relay 5 off after a power cut.", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(board.PowerOnWrites);
        await page.SubmitAsync(PageRenderer.With("id", "group-form"));

        Assert.IsType<NavigationException>(Assert.Single(page.Errors));
        Assert.Equal([(5, PowerOnState.Off)], board.PowerOnWrites);
        Assert.Equal(PowerOnState.On, board.State.PowerOn?.For(1));
    }

    // A save that adds no relay writes nothing: the warning's "Make it safe" is the press for the relays already in it.
    [Fact]
    public async Task Should_WriteNothingOnSave_WithoutNewRelays()
    {
        var board = Board(heat: PowerOnState.On);
        await using var app = App(board);
        var groupId = await AddGroupAsync(app);
        using var page = await OpenGroupAsync(app, groupId);

        await page.TypeAsync(PageRenderer.With("id", "name"), "Fan heater");
        await page.SubmitAsync(PageRenderer.With("id", "group-form"));

        Assert.IsType<NavigationException>(Assert.Single(page.Errors));
        Assert.Empty(board.PowerOnWrites);
    }

    // A save whose write fails keeps the group as saved, stays open, and says so next to Save, with the warning on top.
    [Fact]
    public async Task Should_KeepTheSavedGroup_AndSaySo_WhenTheWriteFails()
    {
        var board = Board(heat: PowerOnState.Off, relay5: PowerOnState.On);
        board.RefusePowerOn = true;
        await using var app = App(board);
        var groupId = await AddGroupAsync(app);
        using var page = await OpenGroupAsync(app, groupId);

        await AddStepAsync(page, relayId: 5);
        await page.SubmitAsync(PageRenderer.With("id", "group-form"));

        Assert.Empty(page.Errors);
        var text = await page.TextAsync();
        Assert.Contains("The group was saved, but Contactor couldn't make sure that the board keeps its new relays off after a power cut.", text, StringComparison.Ordinal);
        // The message doesn't point to a warning that may not be drawn: a write the board took without answering has none.
        Assert.DoesNotContain("See the warning", text, StringComparison.Ordinal);
        Assert.Contains("the board can turn Relay 5 on by itself", text, StringComparison.Ordinal);
        // The steps are this page's own save, not someone else's change: also after the save's notification reloads it.
        await Wait.UntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains("Relay 5", StringComparison.Ordinal));
        Assert.DoesNotContain("Someone else changed the group", await page.TextAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("Someone else changed the group", text, StringComparison.Ordinal);
        await using var db = await Factory(app).CreateDbContextAsync(Cancel);
        Assert.True(await db.RelayGroupMembers.AnyAsync(m => m.GroupId == groupId && m.RelayId == 5, Cancel));
    }

    // A change of the states alone redraws the group's page and the Relays page only when the warning changes; closing
    // the pages lets go.
    [Fact]
    public async Task Should_Redraw_OnlyWhenTheWarningChanges()
    {
        var board = Board(heat: PowerOnState.Off);
        await using var app = App(board);
        var groupId = await AddGroupAsync(app);
        var registry = app.Services.GetRequiredService<BoardRegistry>();
        var group = await OpenGroupAsync(app, groupId);
        var relays = await PageRenderer.StartAsync<Relays>(app.Services, John, "John");
        using (group)
        using (relays)
        {
            var renders = (group.PageRenders, relays.PageRenders);

            // Relay 5 is in no group: nothing to redraw.
            board.SetPowerOn(States(heat: PowerOnState.Off, relay5: PowerOnState.On));
            board.SetPowerOn(States(heat: PowerOnState.On, relay5: PowerOnState.On));
            await Wait.UntilAsync(() => group.HasAsync(PageRenderer.WithText("Make it safe")).GetAwaiter().GetResult()
                && relays.HasAsync(PageRenderer.WithText("Make it safe")).GetAwaiter().GetResult());

            Assert.Equal((renders.Item1 + 1, renders.Item2 + 1), (group.PageRenders, relays.PageRenders));
            Assert.Equal(1, PageRenderer.Handlers<GroupDetail>(registry, nameof(BoardRegistry.PowerOnChanged)));
            Assert.Equal(1, PageRenderer.Handlers<Relays>(registry, nameof(BoardRegistry.PowerOnChanged)));
        }

        Assert.Equal(0, PageRenderer.Handlers<GroupDetail>(registry, nameof(BoardRegistry.PowerOnChanged)));
        Assert.Equal(0, PageRenderer.Handlers<Relays>(registry, nameof(BoardRegistry.PowerOnChanged)));
    }

    [Fact]
    public async Task Should_WarnInSwedish()
    {
        var board = Board(heat: PowerOnState.On);
        await using var app = App(board);
        var groupId = await AddGroupAsync(app);
        await app.Services.GetRequiredService<AppLanguage>().ChooseAsync("sv", Cancel);
        using var http = app.CreateClient();

        var page = WebUtility.HtmlDecode(await http.GetStringAsync($"/group/{groupId}", Cancel));

        Assert.Contains("Efter ett strömavbrott kan kortet slå på Heat av sig självt, utan gruppens startordning.", page, StringComparison.Ordinal);
        Assert.Contains("\"Gör säkert\" ställer det på Av på kortet. Contactor startar sedan gruppen i sin ordning.", page, StringComparison.Ordinal);
        Assert.Contains(">Gör säkert</button>", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_SayTheBoardCantTell_InSwedish()
    {
        await using var app = App(new FakeRelayBoard());
        var groupId = await AddGroupAsync(app);
        await app.Services.GetRequiredService<AppLanguage>().ChooseAsync("sv", Cancel);
        using var http = app.CreateClient();

        var page = WebUtility.HtmlDecode(await http.GetStringAsync($"/group/{groupId}", Cancel));

        Assert.Contains("Kan inte se vad kortet gör efter ett strömavbrott. Kontrollera det på kortet.", page, StringComparison.Ordinal);
    }

    // A relay in a group must be Off after a power cut: the Relay screen's refusal doesn't claim what the board does.
    [Fact]
    public async Task Should_RefuseARelayInAGroup_WithoutClaimingTheBoardKeepsItOff()
    {
        var board = Board(heat: PowerOnState.On);
        await using var app = App(board);
        await AddGroupAsync(app);
        var handler = app.Services.GetRequiredService<RelayPowerOnHandler>();

        var english = await handler.SetAsync(3, PowerOnState.Off, John, Cancel);
        string? swedish;
        using (TestCulture.Language("sv"))
        {
            swedish = await handler.SetAsync(3, PowerOnState.Off, John, Cancel);
        }

        Assert.Equal("This relay is in a group, so it must be Off after a power cut. Its group's page shows what the board does.", english);
        Assert.Equal("Reläet ingår i en grupp, så det måste vara av efter ett strömavbrott. Gruppens sida visar vad kortet gör.", swedish);
        Assert.Empty(board.PowerOnWrites);
    }

    // An unknown state gives no red warning and "Make it safe" doesn't touch it: only a quiet note on the group's page,
    // until the board reports it. Nothing is written without a press.
    [Fact]
    public async Task Should_NoteAnUnknownState_UntilTheBoardReportsIt()
    {
        var board = Board(heat: null);
        await using var app = App(board);
        var groupId = await AddGroupAsync(app);
        using var page = await OpenGroupAsync(app, groupId);
        Assert.Contains("Can't see yet what the board does after a power cut.", await page.TextAsync(), StringComparison.Ordinal);
        Assert.False(await page.HasAsync(PageRenderer.WithText("Make it safe")));
        Assert.Null(await app.Services.GetRequiredService<RelayPowerOnHandler>().MakeGroupSafeAsync(groupId, John, relayIds: null, onSave: false, Cancel));

        board.SetPowerOn(States(heat: PowerOnState.Off));
        await Wait.UntilAsync(() => !page.TextAsync().GetAwaiter().GetResult().Contains("Can't see yet", StringComparison.Ordinal));

        Assert.Empty(board.PowerOnWrites);
        Assert.Empty(page.Errors);
    }

    // "Create group" also writes Off for a relay whose state is unknown, and the review names it.
    [Fact]
    public async Task Should_SetAnUnknownRelayToOff_OnCreate()
    {
        var board = Board(heat: null);
        await using var app = App(board);
        using var page = await AtReviewAsync(app, 2, 3);
        Assert.Contains("Contactor sets the board to keep Relay 3 off after a power cut.", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(board.PowerOnWrites);

        await page.ClickAsync(Button("Create group"));

        Assert.IsType<NavigationException>(Assert.Single(page.Errors));
        Assert.Equal([(3, PowerOnState.Off)], board.PowerOnWrites);
        var logged = Assert.Single(await PowerOnEventsAsync(app));
        Assert.Equal((3, """{"to":"off"}"""), (logged.RelayId, logged.Details));
    }

    // A board that hasn't reported any state: every chosen relay is unknown, and all of them are written.
    [Fact]
    public async Task Should_SetEveryRelayToOff_OnCreate_BeforeTheBoardReported()
    {
        var board = new FakeRelayBoard(powerOn: AllStates);
        await using var app = App(board);
        using var page = await AtReviewAsync(app, 2, 3);
        Assert.Contains("Contactor sets the board to keep Relay 2 and Relay 3 off after a power cut.", await page.TextAsync(), StringComparison.Ordinal);

        await page.ClickAsync(Button("Create group"));

        Assert.Equal([(2, PowerOnState.Off), (3, PowerOnState.Off)], board.PowerOnWrites);
    }

    // Save writes Off for an added relay whose state is unknown, and only for it.
    [Fact]
    public async Task Should_SetAnAddedUnknownRelayToOff_OnSave()
    {
        var board = new FakeRelayBoard(powerOn: AllStates);
        board.SetPowerOn(new PowerOnStates([PowerOnState.Off, PowerOnState.Off, PowerOnState.Off, PowerOnState.Off, null, PowerOnState.Off, PowerOnState.Off, PowerOnState.Off]));
        await using var app = App(board);
        var groupId = await AddGroupAsync(app);
        using var page = await OpenGroupAsync(app, groupId);

        await AddStepAsync(page, relayId: 5);
        Assert.Contains("When you save, Contactor sets the board to keep Relay 5 off after a power cut.", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(board.PowerOnWrites);
        await page.SubmitAsync(PageRenderer.With("id", "group-form"));

        Assert.IsType<NavigationException>(Assert.Single(page.Errors));
        Assert.Equal([(5, PowerOnState.Off)], board.PowerOnWrites);
    }

    [Fact]
    public async Task Should_NoteAnUnknownState_InSwedish()
    {
        await using var app = App(Board(heat: null));
        var groupId = await AddGroupAsync(app);
        await app.Services.GetRequiredService<AppLanguage>().ChooseAsync("sv", Cancel);
        using var http = app.CreateClient();

        var page = WebUtility.HtmlDecode(await http.GetStringAsync($"/group/{groupId}", Cancel));

        Assert.Contains("Kan inte se än vad kortet gör efter ett strömavbrott.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Gör säkert", page, StringComparison.Ordinal);
    }

    // Relays 1 to 8, with relay 2 (Fan) and 3 (Heat) as given and every other Off, unless relay 5's is given.
    private static FakeRelayBoard Board(PowerOnState? heat, PowerOnState fan = PowerOnState.Off, PowerOnState relay5 = PowerOnState.Off)
    {
        var board = new FakeRelayBoard(powerOn: AllStates);
        board.SetPowerOn(States(heat, fan, relay5));
        return board;
    }

    private static PowerOnStates States(PowerOnState? heat, PowerOnState fan = PowerOnState.Off, PowerOnState relay5 = PowerOnState.Off)
    {
        return new PowerOnStates([PowerOnState.Off, fan, heat, PowerOnState.Off, relay5, PowerOnState.Off, PowerOnState.Off, PowerOnState.Off]);
    }

    private static TestWebApp App(FakeRelayBoard board)
    {
        return new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John },
            TestWebApp.WithBoard(board));
    }

    // A group of relays 2 ("Fan") and 3 ("Heat"), each a step of its own.
    private static async Task<int> AddGroupAsync(TestWebApp app)
    {
        using (var http = app.CreateClient())
        {
            await http.GetAsync("/", Cancel);
        }

        await using var db = await Factory(app).CreateDbContextAsync(Cancel);
        var group = new RelayGroup { BoardId = 1, Name = "Heater", Icon = GroupIcons.Flame };
        group.AddStep("Fan", [2]);
        group.AddStep("Heat", [3]);
        await db.AddWithRuntimesAsync(group);
        return group.Id;
    }

    private static Task<PageRenderer> OpenGroupAsync(TestWebApp app, int groupId)
    {
        return PageRenderer.StartAsync<GroupDetail>(app.Services, John, "John", new Dictionary<string, object?> { [nameof(GroupDetail.Id)] = groupId });
    }

    // "+ Add a step" with the relay.
    private static async Task AddStepAsync(PageRenderer page, int relayId)
    {
        await page.ClickAsync(Button("Add a step"));
        await page.TypeAsync(PageRenderer.With("id", $"add-relay-{GroupEditor.NewStep}"), relayId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await page.ClickAsync(Button("Add"));
    }

    // "New group" on "Review and create", with these relays picked in this order.
    private static async Task<PageRenderer> AtReviewAsync(TestWebApp app, params int[] relayIds)
    {
        using (var http = app.CreateClient())
        {
            await http.GetAsync("/", Cancel);
        }

        var page = await PageRenderer.StartAsync<GroupNew>(app.Services, John, "John");
        await page.TypeAsync(PageRenderer.With("id", "name"), "Heater");
        await page.ClickAsync(Button("Next"));
        foreach (var id in relayIds)
        {
            await page.ClickAsync(PageRenderer.With("id", $"pick-{id}"));
        }

        await page.ClickAsync(Button("Next"));
        await page.ClickAsync(Button("Next"));
        Assert.Contains("Review and create", await page.TextAsync(), StringComparison.Ordinal);
        return page;
    }

    private static async Task<int> SingleGroupIdAsync(TestWebApp app)
    {
        await using var db = await Factory(app).CreateDbContextAsync(Cancel);
        return await db.RelayGroups.Select(g => g.Id).SingleAsync(Cancel);
    }

    private static async Task<List<EventLogEntry>> PowerOnEventsAsync(TestWebApp app)
    {
        await using var db = await Factory(app).CreateDbContextAsync(Cancel);
        return await db.EventLog.AsNoTracking().Where(e => e.Action == EventActions.PowerOnSet).ToListAsync(Cancel);
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool> Button(string text)
    {
        return attributes => attributes.ContainsKey("type") && PageRenderer.WithText(text)(attributes);
    }

    private static IDbContextFactory<ContactorDbContext> Factory(TestWebApp app)
    {
        return app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
    }
}
