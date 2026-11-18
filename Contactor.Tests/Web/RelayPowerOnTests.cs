using System.Net;
using Contactor.Boards;
using Contactor.Tests.Boards;
using Contactor.Web.Components;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Languages;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// "After a power cut" on the Relay screen (Issue #516): what the board does with a relay by itself when the power comes
/// back, shown from the board and written to it only by a person's Save, and the event it logs.
/// </summary>
public sealed class RelayPowerOnTests
{
    private const string John = "john@example.com";

    private static readonly PowerOnAbility AllStates = new([PowerOnState.Off, PowerOnState.AsBefore, PowerOnState.On, PowerOnState.FollowsInput], perRelay: true);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ShowTheBoardsState_WithEachStateInPlainWords()
    {
        var board = Board();
        await using var app = App(board);
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/relay/3", Cancel));

        Assert.Contains("After a power cut", html, StringComparison.Ordinal);
        Assert.Contains("What the board does with this relay by itself when the power comes back, before Contactor runs.", html, StringComparison.Ordinal);
        Assert.Contains("The board keeps it off until Contactor runs.", html, StringComparison.Ordinal);
        Assert.Contains("The board puts it back by itself as it was when the power went, on or off.", html, StringComparison.Ordinal);
        Assert.Contains("The board turns it on by itself, without Contactor.", html, StringComparison.Ordinal);
        Assert.Contains("The board turns it on by itself while its switch is on, without Contactor.", html, StringComparison.Ordinal);
        Assert.Matches("value=\"On\" aria-pressed=\"true\"[^>]*><span class=\"power-name\"[^>]*>On</span>", html);
        Assert.Equal(1, CountPressed(html));
        Assert.DoesNotContain("Can't see yet", html, StringComparison.Ordinal);
        Assert.DoesNotContain("every relay on the board", html, StringComparison.Ordinal);
        Assert.DoesNotContain("can't be changed now", html, StringComparison.Ordinal);
    }

    // Only the states the board has; for a board whose state is for every relay, the page says so.
    [Fact]
    public async Task Should_OfferTheBoardsStatesOnly_AndSayWhenOneIsForTheWholeBoard()
    {
        var board = new FakeRelayBoard(powerOn: new PowerOnAbility([PowerOnState.Off, PowerOnState.AsBefore], perRelay: false));
        await using var app = App(board);
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/relay/3", Cancel));

        Assert.Contains("value=\"Off\"", html, StringComparison.Ordinal);
        Assert.Contains("value=\"AsBefore\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("value=\"On\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Follows its switch", html, StringComparison.Ordinal);
        Assert.Contains("This applies to every relay on the board.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_SayTheStateIsUnknown_WhileTheBoardHasntReportedIt()
    {
        var board = new FakeRelayBoard(powerOn: AllStates);
        await using var app = App(board);
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/relay/3", Cancel));

        Assert.Contains("Can't see yet what the board does with this relay after a power cut.", html, StringComparison.Ordinal);
        Assert.Equal(0, CountPressed(html));
    }

    [Fact]
    public async Task Should_ShowNoField_ForABoardWithoutTheAbility()
    {
        await using var app = App(new FakeRelayBoard());
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/relay/3", Cancel));

        Assert.DoesNotContain("After a power cut", html, StringComparison.Ordinal);
        Assert.DoesNotContain("power-choices", html, StringComparison.Ordinal);
    }

    // A relay in a group gets its state on the group's page.
    [Fact]
    public async Task Should_ShowNoField_ForARelayInAGroup_AndRefuseToSetIt()
    {
        var board = Board();
        await using var app = App(board);
        await AddGroupAsync(app);
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/relay/3", Cancel));
        var error = await app.Services.GetRequiredService<RelayPowerOnHandler>().SetAsync(3, PowerOnState.Off, John, Cancel);

        Assert.DoesNotContain("After a power cut", html, StringComparison.Ordinal);
        Assert.Equal("This relay is in a group: set what it does after a power cut on the group's page.", error);
        Assert.Empty(board.PowerOnWrites);
        Assert.Empty(await PowerOnEventsAsync(app));
    }

    [Fact]
    public async Task Should_WriteThePersonsChoice_OnSave_AndLogIt()
    {
        var board = Board();
        await using var app = App(board);
        using (var page = await OpenAsync(app))
        {
            await page.ClickAsync(PageRenderer.With("value", "AsBefore"));
            Assert.Empty(board.PowerOnWrites);

            await page.SubmitAsync(PageRenderer.With("id", "relay-form"));

            Assert.IsType<NavigationException>(Assert.Single(page.Errors));
        }

        Assert.Equal([(3, PowerOnState.AsBefore)], board.PowerOnWrites);
        Assert.Equal(PowerOnState.AsBefore, board.State.PowerOn?.For(3));
        var logged = Assert.Single(await PowerOnEventsAsync(app));
        Assert.Equal((John, 3, 1, """{"from":"on","to":"as-before"}"""), (logged.Actor, logged.RelayId, logged.BoardId, logged.Details));

        // Shown with the relay's latest events, as the others are.
        using var http = app.CreateClient();
        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/relay/3", Cancel));
        Assert.Contains("set the relay to go back as it was after a power cut", html, StringComparison.Ordinal);
    }

    // Nothing goes to the board without a person's choice, nor for a choice that is what the board already has.
    [Theory]
    [InlineData(null)]
    [InlineData("On")]
    public async Task Should_WriteNothing_WhenTheChoiceDidntChange(string? pressed)
    {
        var board = Board();
        await using var app = App(board);
        using (var page = await OpenAsync(app))
        {
            if (pressed is not null)
            {
                await page.ClickAsync(PageRenderer.With("value", pressed));
            }

            await page.SubmitAsync(PageRenderer.With("id", "relay-form"));

            Assert.IsType<NavigationException>(Assert.Single(page.Errors));
        }

        Assert.Empty(board.PowerOnWrites);
        Assert.Empty(await PowerOnEventsAsync(app));
    }

    [Fact]
    public async Task Should_LetNothingChange_WithoutContact()
    {
        var board = Board();
        board.SetConnected(false);
        await using var app = App(board);
        using var http = app.CreateClient();
        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/relay/3", Cancel));

        using (var page = await OpenAsync(app))
        {
            await page.ClickAsync(PageRenderer.With("value", "Off"));
            await page.SubmitAsync(PageRenderer.With("id", "relay-form"));
            Assert.IsType<NavigationException>(Assert.Single(page.Errors));
        }

        Assert.Contains("No contact with the board, so this can't be changed now.", html, StringComparison.Ordinal);
        Assert.Matches("value=\"Off\" aria-pressed=\"false\" disabled", html);
        Assert.Empty(board.PowerOnWrites);
    }

    // A choice made while the board answered, and the contact lost before Save: the rest is saved, nothing is queued, and
    // the page says why.
    [Fact]
    public async Task Should_SayThereIsNoContact_WhenItWasLostBeforeSave()
    {
        var board = Board();
        await using var app = App(board);
        using var page = await OpenAsync(app);
        await page.ClickAsync(PageRenderer.With("value", "Off"));
        await page.TypeAsync(PageRenderer.With("id", "name"), "Heat");
        board.SetConnected(false);

        await page.SubmitAsync(PageRenderer.With("id", "relay-form"));

        Assert.Empty(page.Errors);
        Assert.Contains("No contact with the board, so what it does after a power cut wasn't changed. Try again when it answers.", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(board.PowerOnWrites);
        Assert.Empty(await PowerOnEventsAsync(app));
        await using var db = await Factory(app).CreateDbContextAsync(Cancel);
        Assert.Equal("Heat", (await db.Relays.SingleAsync(r => r.Id == 3, Cancel)).Name);
    }

    [Fact]
    public async Task Should_SayTheBoardDidntTakeIt_AndLogNothing()
    {
        var board = Board();
        board.RefusePowerOn = true;
        await using var app = App(board);
        using var page = await OpenAsync(app);
        await page.ClickAsync(PageRenderer.With("value", "Off"));

        await page.SubmitAsync(PageRenderer.With("id", "relay-form"));

        Assert.Empty(page.Errors);
        Assert.Contains("The board didn't take the change. Try again.", await page.TextAsync(), StringComparison.Ordinal);
        Assert.Empty(await PowerOnEventsAsync(app));

        // Save again once the board takes it: the choice was kept.
        board.RefusePowerOn = false;
        await page.SubmitAsync(PageRenderer.With("id", "relay-form"));

        Assert.IsType<NavigationException>(Assert.Single(page.Errors));
        Assert.Equal([(3, PowerOnState.Off)], board.PowerOnWrites);
        Assert.Single(await PowerOnEventsAsync(app));
    }

    // A choice made on one relay's page isn't carried to another relay's when the page opens that one.
    [Fact]
    public async Task Should_ForgetTheChoice_WhenThePageShowsAnotherRelay()
    {
        var board = Board();
        await using var app = App(board);
        using var page = await OpenAsync(app);
        await page.ClickAsync(PageRenderer.With("value", "AsBefore"));

        await page.SetParametersAsync(new Dictionary<string, object?> { [nameof(RelayDetail.Id)] = 5 });
        await page.SubmitAsync(PageRenderer.With("id", "relay-form"));

        Assert.IsType<NavigationException>(Assert.Single(page.Errors));
        Assert.Empty(board.PowerOnWrites);
    }

    // A change of the states alone redraws the page only when the relay's own changed; closing the page lets go.
    [Fact]
    public async Task Should_Redraw_OnlyWhenTheRelaysStateChanged()
    {
        var board = Board();
        await using var app = App(board);
        var registry = app.Services.GetRequiredService<BoardRegistry>();
        var page = await OpenAsync(app);
        using (page)
        {
            var renders = page.PageRenders;

            board.SetPowerOn(States(relay3: PowerOnState.On, relay5: PowerOnState.On));
            board.SetPowerOn(States(relay3: PowerOnState.Off, relay5: PowerOnState.On));
            await Wait.UntilAsync(() => page.HasAsync(a => a.TryGetValue("value", out var v) && "Off".Equals(v) && "true".Equals(a.GetValueOrDefault("aria-pressed"))).GetAwaiter().GetResult());

            Assert.Equal(renders + 1, page.PageRenders);
            Assert.Equal(1, PageRenderer.Handlers<RelayDetail>(registry, nameof(BoardRegistry.PowerOnChanged)));
        }

        Assert.Equal(0, PageRenderer.Handlers<RelayDetail>(registry, nameof(BoardRegistry.PowerOnChanged)));
    }

    [Theory]
    [InlineData("""{"from":"on","to":"off"}""", "set the relay to stay off after a power cut")]
    [InlineData("""{"to":"as-before"}""", "set the relay to go back as it was after a power cut")]
    [InlineData("""{"to":"on"}""", "set the relay to turn on after a power cut")]
    [InlineData("""{"to":"follows-input"}""", "set the relay to follow its switch after a power cut")]
    [InlineData("""{"to":"later"}""", "changed what the relay does after a power cut")]
    [InlineData(null, "changed what the relay does after a power cut")]
    public void Should_DescribeThePersonsChange(string? details, string what)
    {
        var view = TestTexts.Events.For(Entry(details), "Heat", "Erik");

        Assert.Equal(new EventView(true, "E", "Erik", "{0} " + what, "Heat"), view);
    }

    [Theory]
    [InlineData("""{"to":"off"}""", "Erik ställde in att reläet förblir av efter ett strömavbrott")]
    [InlineData("""{"to":"as-before"}""", "Erik ställde in att reläet blir som innan efter ett strömavbrott")]
    [InlineData("""{"to":"on"}""", "Erik ställde in att reläet slås på efter ett strömavbrott")]
    [InlineData("""{"to":"follows-input"}""", "Erik ställde in att reläet följer sin brytare efter ett strömavbrott")]
    [InlineData("{", "Erik ändrade vad reläet gör efter ett strömavbrott")]
    public void Should_DescribeThePersonsChange_InSwedish(string details, string text)
    {
        using var swedish = TestCulture.Language("sv");

        Assert.Equal(text, TestTexts.Events.For(Entry(details), "Värme", "Erik").Text);
    }

    [Fact]
    public async Task Should_ShowTheFieldInSwedish()
    {
        var board = Board();
        await using var app = App(board);
        using var http = app.CreateClient();
        await http.GetAsync("/", Cancel);
        await app.Services.GetRequiredService<AppLanguage>().ChooseAsync("sv", Cancel);

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/relay/3", Cancel));

        Assert.Contains("Efter ett strömavbrott", html, StringComparison.Ordinal);
        Assert.Contains("Som innan", html, StringComparison.Ordinal);
        Assert.Contains("Följer sin brytare", html, StringComparison.Ordinal);
        Assert.Contains("Kortet slår på det själv, utan Contactor.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_SaveEachState_AsAName_AndReadItBack()
    {
        foreach (var state in Enum.GetValues<PowerOnState>())
        {
            Assert.True(PowerOnStateNames.TryParse(PowerOnStateNames.For(state), out var read));
            Assert.Equal(state, read);
        }

        Assert.False(PowerOnStateNames.TryParse("On", out _));
        Assert.False(PowerOnStateNames.TryParse(null, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => PowerOnStateNames.For((PowerOnState)99));
    }

    // A board with every state, whose relay 3 turns on after a power cut.
    private static FakeRelayBoard Board()
    {
        var board = new FakeRelayBoard(powerOn: AllStates);
        board.SetPowerOn(States(relay3: PowerOnState.On, relay5: PowerOnState.Off));
        return board;
    }

    private static PowerOnStates States(PowerOnState relay3, PowerOnState relay5)
    {
        PowerOnState?[] states = [PowerOnState.Off, PowerOnState.Off, relay3, PowerOnState.Off, relay5, PowerOnState.Off, PowerOnState.Off, PowerOnState.Off];
        return new PowerOnStates(states);
    }

    private static TestWebApp App(FakeRelayBoard board)
    {
        return new TestWebApp("Development", new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John }, TestWebApp.WithBoard(board));
    }

    private static Task<PageRenderer> OpenAsync(TestWebApp app)
    {
        return PageRenderer.StartAsync<RelayDetail>(app.Services, John, "John", new Dictionary<string, object?> { [nameof(RelayDetail.Id)] = 3 });
    }

    private static async Task AddGroupAsync(TestWebApp app)
    {
        await using var db = await Factory(app).CreateDbContextAsync(Cancel);
        var group = new RelayGroup { BoardId = 1, Name = "Heater", Icon = GroupIcons.Flame };
        group.AddStep("Fan", [2]);
        group.AddStep("Heat", [3]);
        await db.AddWithRuntimesAsync(group);
    }

    private static async Task<List<EventLogEntry>> PowerOnEventsAsync(TestWebApp app)
    {
        await using var db = await Factory(app).CreateDbContextAsync(Cancel);
        return await db.EventLog.AsNoTracking().Where(e => e.Action == EventActions.PowerOnSet).ToListAsync(Cancel);
    }

    // The choices pressed on a page: a power-cut state's button is the only one whose name is in a span.
    private static int CountPressed(string html)
    {
        return System.Text.RegularExpressions.Regex.Count(html, "aria-pressed=\"true\"[^>]*><span class=\"power-name\"");
    }

    private static EventLogEntry Entry(string? details)
    {
        return new EventLogEntry { Actor = "erik@example.com", Action = EventActions.PowerOnSet, Details = details, RelayId = 3 };
    }

    private static IDbContextFactory<ContactorDbContext> Factory(TestWebApp app)
    {
        return app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
    }
}
