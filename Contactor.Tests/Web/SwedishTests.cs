using System.Globalization;
using System.Net;
using Contactor.Web.Components;
using Contactor.Web.Features.Languages;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// The app in Swedish: its pages' texts, and the dates and numbers in them in Swedish formats, whatever the computer's
/// own language.
/// </summary>
public sealed class SwedishTests
{
    private const string John = "john@example.com";
    private static readonly DateTime Now = new(2026, 11, 4, 15, 30, 0);

    // Now, in the app's time zone (Europe/Stockholm): 15:30.
    private static readonly DateTimeOffset NowUtc = new(2026, 11, 4, 14, 30, 0, TimeSpan.Zero);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_HaveSwedish_ByItsOwnName()
    {
        Assert.Equal(["English", "Svenska"], LanguageCatalogue.Default.All.Select(l => l.Name));
        Assert.Equal("sv", LanguageCatalogue.Default.Find("sv")?.Code);
    }

    [Fact]
    public async Task Should_ShowTheRelaysPage_InSwedish()
    {
        await using var app = await SwedishAppAsync();
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        Assert.Contains("<html lang=\"sv\">", html, StringComparison.Ordinal);
        Assert.Contains("<title>Reläer · Contactor</title>", html, StringComparison.Ordinal);
        Assert.Contains("Hej John", html, StringComparison.Ordinal);
        Assert.Contains("0 av 8 på", html, StringComparison.Ordinal);
        Assert.Contains("Allt av", html, StringComparison.Ordinal);
        Assert.Contains("Ny grupp", html, StringComparison.Ordinal);
        Assert.DoesNotContain("All off", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ShowARelaysPage_AndItsEvents_InSwedish()
    {
        // On a clock of the test's own: "I dag" whatever the time of day the test runs.
        var time = new FixedTimeProvider(NowUtc);
        await using var app = await SwedishAppAsync(time);
        using var http = app.CreateClient();
        await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel))
        {
            db.EventLog.Add(new EventLogEntry { TimestampUtc = time.GetUtcNow().UtcDateTime, RelayId = 3, Actor = John, Action = EventActions.On });
            await db.SaveChangesAsync(Cancel);
        }

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/relay/3", Cancel));

        Assert.Contains("RELÄ 3 · NAMN", html, StringComparison.Ordinal);
        Assert.Contains("SLÅ PÅ", html, StringComparison.Ordinal);
        Assert.Matches("aria-pressed=\"true\"[^>]*>2 tim</button>", html);
        // The name in bold where the Swedish line puts it, and the day in Swedish.
        Assert.Matches("<strong[^>]*>John</strong> slog på", html);
        Assert.Contains("I dag", html, StringComparison.Ordinal);
        Assert.Contains("15:30", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_NameTheIcons_AndANewBoard_InSwedish()
    {
        await using var app = await SwedishAppAsync();
        using var http = app.CreateClient();

        var group = WebUtility.HtmlDecode(await http.GetStringAsync("/group/new", Cancel));
        var board = WebUtility.HtmlDecode(await http.GetStringAsync("/board/new", Cancel));

        Assert.Matches("<span[^>]*>Fläkt</span>", group);
        Assert.Matches("<span[^>]*>Verktyg</span>", group);
        Assert.DoesNotMatch("<span[^>]*>Tools</span>", group);
        Assert.Contains("placeholder=\"T.ex. Garage\"", board, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_OfferEveryLanguage_ByItsOwnName_InSwedish()
    {
        await using var app = await SwedishAppAsync();
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));

        Assert.Contains("Ett språk för alla som använder appen.", html, StringComparison.Ordinal);
        Assert.Matches("<option value=\"en\" lang=\"en\"( b-[a-z0-9]+)?>English</option>", html);
        Assert.Matches("<option value=\"sv\" lang=\"sv\" selected[^>]*>Svenska</option>", html);
    }

    // A board's change comes on a thread of its own, in English: the page it redraws stays Swedish.
    [Fact]
    public async Task Should_StayInSwedish_WhenAChangeFromAnotherThreadRedrawsThePage()
    {
        await using var app = await SwedishAppAsync();
        PageRenderer page;
        using (TestCulture.Language("sv"))
        {
            page = await PageRenderer.StartAsync<Relays>(app.Services, John, "John");
        }

        using (page)
        {
            var renders = page.Renders;
            Assert.Equal("en", CultureInfo.CurrentUICulture.Name);

            app.Services.GetRequiredService<RelayChanges>().Notify();
            await Wait.UntilAsync(() => page.Renders > renders);

            var text = await page.TextAsync();
            Assert.Contains("Hej John", text, StringComparison.Ordinal);
            Assert.Contains("Allt av", text, StringComparison.Ordinal);
            Assert.DoesNotContain("All off", text, StringComparison.Ordinal);
            Assert.Empty(page.Errors);
        }
    }

    // As for the Relays page: a group's page and the tab bar redrawn by a change from another thread stay Swedish.
    [Fact]
    public async Task Should_KeepAGroupsPageInSwedish_WhenAChangeFromAnotherThreadRedrawsIt()
    {
        await using var app = await SwedishAppAsync();
        var factory = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        int id;
        await using (var db = await factory.CreateDbContextAsync(Cancel))
        {
            // The group and its runtime in one transaction, as the app saves a new group: the running group sequencer
            // makes a runtime for a group it finds without one.
            await using var transaction = await db.Database.BeginTransactionAsync(Cancel);
            var group = new RelayGroup { BoardId = 1, Name = "Fläktvärmare", Icon = GroupIcons.Fan, MaxOnMinutes = 120 };
            group.AddStep("Fläkt", 1, runOnSec: 300);
            group.AddStep("Värme", 2);
            db.RelayGroups.Add(group);
            await db.SaveChangesAsync(Cancel);
            db.GroupRuntimes.Add(new GroupRuntime { GroupId = group.Id });
            await db.SaveChangesAsync(Cancel);
            await transaction.CommitAsync(Cancel);
            id = group.Id;
        }

        PageRenderer page;
        using (TestCulture.Language("sv"))
        {
            page = await PageRenderer.StartAsync<GroupDetail>(app.Services, John, "John", new Dictionary<string, object?> { ["Id"] = id });
        }

        using (page)
        {
            await AssertSwedishAfterRedrawAsync(app, page, "SLÅ PÅ GRUPPEN", "TURN ON THE GROUP");
        }
    }

    [Fact]
    public async Task Should_KeepTheTabBarInSwedish_WhenAChangeFromAnotherThreadRedrawsIt()
    {
        await using var app = await SwedishAppAsync();
        PageRenderer page;
        using (TestCulture.Language("sv"))
        {
            page = await PageRenderer.StartAsync<TabBar>(app.Services, John, "John");
        }

        using (page)
        {
            await AssertSwedishAfterRedrawAsync(app, page, "Inställningar", "Settings");
        }
    }

    [Fact]
    public async Task Should_PutTheLanguageBack_AfterUsingIt()
    {
        await using var app = await SwedishAppAsync();
        var language = app.Services.GetRequiredService<AppLanguage>();

        using (language.Use())
        {
            Assert.Equal(("sv", "sv"), (CultureInfo.CurrentCulture.Name, CultureInfo.CurrentUICulture.Name));
        }

        Assert.Equal(("en", "en"), (CultureInfo.CurrentCulture.Name, CultureInfo.CurrentUICulture.Name));
    }

    [Theory]
    [InlineData(2026, 11, 1, "1 nov. 08:00")]
    [InlineData(2026, 5, 31, "31 maj 08:00")]
    [InlineData(2026, 11, 3, "i går 08:00")]
    public void Should_GiveARelaysTimes_WithSwedishDays(int year, int month, int day, string expected)
    {
        using var swedish = TestCulture.Language("sv");

        Assert.Equal(expected, TestTexts.RelayStatus.When(new DateTime(year, month, day, 8, 0, 0), Now));
    }

    [Fact]
    public void Should_SayARelaysStatus_InSwedish()
    {
        using var swedish = TestCulture.Language("sv");

        var text = TestTexts.RelayStatus.For(true, new DateTime(2026, 11, 4, 14, 2, 0), "Jane", new DateTime(2026, 11, 5, 1, 10, 0), Now);

        Assert.Equal("På sedan 14:02 · Jane · stängs av i morgon 01:10", text);
    }

    [Fact]
    public void Should_NameTheEventsDays_InSwedish()
    {
        using var swedish = TestCulture.Language("sv");

        Assert.Equal("MÅNDAG 2 NOV.", TestTexts.EventsHandler.Heading(new DateTime(2026, 11, 2), Now.Date));
        Assert.Equal("I GÅR", TestTexts.EventsHandler.Heading(new DateTime(2026, 11, 3), Now.Date));
    }

    [Fact]
    public void Should_WriteAnEventsLine_InSwedish()
    {
        using var swedish = TestCulture.Language("sv");

        var view = TestTexts.Events.For(new EventLogEntry { Actor = EventActors.AutoOff, Action = EventActions.Off, RelayId = 1 }, "Kompressor", null);

        Assert.Equal(("Automatisk avstängning", "{0} stängde av efter en stund"), (view.Who, view.Line));
    }

    [Theory]
    [InlineData(68 * 1024, "68 kB")]
    [InlineData(1363149, "1,3 MB")]
    public void Should_GiveABackupsSize_WithADecimalComma(long bytes, string size)
    {
        using var swedish = TestCulture.Language("sv");

        Assert.Equal(size, Settings.Size(TestTexts.Localizer, bytes));
    }

    // A change from a thread of the test's own, in English (the relays' "In use", a group made): the page redraws, and
    // its texts stay Swedish.
    private static async Task AssertSwedishAfterRedrawAsync(TestWebApp app, PageRenderer page, string swedish, string english)
    {
        var renders = page.Renders;
        Assert.Equal("en", CultureInfo.CurrentUICulture.Name);

        app.Services.GetRequiredService<RelayChanges>().Notify();
        await Wait.UntilAsync(() => page.Renders > renders);

        var text = await page.TextAsync();
        Assert.Contains(swedish, text, StringComparison.Ordinal);
        Assert.DoesNotContain(english, text, StringComparison.Ordinal);
        Assert.Empty(page.Errors);
    }

    // Why the board refuses the connection, in Swedish: on Relays, on Settings and in the side menu.
    [Fact]
    public async Task Should_SayWhyTheBoardRefuses_InSwedish()
    {
        var board = Contactor.Tests.Boards.FakeRelayBoard.LostContact();
        board.SetProblem(new Contactor.Boards.ConnectionProblem(Contactor.Boards.ConnectionProblemKind.LoginRefused, Contactor.Boards.Devantech.DevantechModels.Eth008));
        await using var app = await SwedishAppAsync(board: board);
        using var http = app.CreateClient();

        var relays = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));
        var settings = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));

        Assert.Contains("Lösenordet är fel. Se Inställningar.", relays, StringComparison.Ordinal);
        Assert.Matches("<span class=\"no-contact\"[^>]*>Fel lösenord</span>", relays);
        Assert.Contains("Lösenordet är fel.", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("The password is wrong.", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Ingen kontakt med reläkortet på", settings, StringComparison.Ordinal);
    }

    // The app with Swedish chosen; every request has it, whatever the browser asks for. With a clock, the app runs on
    // it.
    private static async Task<TestWebApp> SwedishAppAsync(TimeProvider? time = null, Contactor.Tests.Boards.FakeRelayBoard? board = null)
    {
        var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John },
            services =>
            {
                TestWebApp.WithBoard(board ?? Contactor.Tests.Boards.FakeRelayBoard.LostContact())(services);
                if (time is not null)
                {
                    services.AddSingleton(time);
                }
            });
        using (var http = app.CreateClient())
        {
            await http.GetAsync("/", Cancel);
        }

        await app.Services.GetRequiredService<AppLanguage>().ChooseAsync("sv", Cancel);
        return app;
    }
}
