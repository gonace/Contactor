using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>Relay groups on the screens, as the server first renders them, with no board connected.</summary>
public sealed class GroupPagesTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ShowTheGroupAsOneCard_WithoutItsRelaysOnTheirOwn()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        await http.GetStringAsync("/", Cancel);
        await AddFanHeaterAsync(app);

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        Assert.Contains("GROUP · RELAY 1 → 2", html, StringComparison.Ordinal);
        Assert.Contains("Fan heater – Lathe", html, StringComparison.Ordinal);
        Assert.Matches("<span class=\"chip[^\"]*\"[^>]*>.*Fan", html);
        Assert.Contains("aria-label=\"Turn on Fan heater – Lathe\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-label=\"Turn on Relay 1\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-label=\"Turn on Relay 2\"", html, StringComparison.Ordinal);
        Assert.Contains("0 of 7 on", html, StringComparison.Ordinal);
        Assert.Contains("<svg class=\"group-icon\"", html, StringComparison.Ordinal);
    }

    // Before the board's first answer the group's card and its screen are grey, with a "–" for every button.
    [Fact]
    public async Task Should_ShowTheGroupAsUnknown_BeforeTheBoardsFirstAnswer()
    {
        await using var app = CreateApp(answered: false);
        using var http = app.CreateClient();
        await http.GetStringAsync("/", Cancel);
        var id = await AddFanHeaterAsync(app);

        var list = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));
        var screen = WebUtility.HtmlDecode(await http.GetStringAsync($"/group/{id}", Cancel));

        Assert.Contains("aria-label=\"Fan heater – Lathe: Waiting for the board\"", list, StringComparison.Ordinal);
        Assert.Contains("<section class=\"status unknown\"", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("TURN ON THE GROUP", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("Needs Fan to be on", screen, StringComparison.Ordinal);
        Assert.Equal(2, screen.Split("class=\"member-toggle unknown\"").Length - 1);
    }

    [Fact]
    public async Task Should_ShowTheGroupsRelaysInOrder_WithTheInterlock()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        await http.GetStringAsync("/", Cancel);
        var id = await AddFanHeaterAsync(app);

        var html = WebUtility.HtmlDecode(await http.GetStringAsync($"/group/{id}", Cancel));

        Assert.Contains("Steps in start order", html, StringComparison.Ordinal);
        Assert.Contains("STEP 1 · NAME", html, StringComparison.Ordinal);
        Assert.Contains("Off · starts first", html, StringComparison.Ordinal);
        Assert.Matches("type=\"radio\"[^>]*value=\"fan\" checked", html);
        Assert.Matches("value=\"Fan\"[\\s\\S]*value=\"Heat\"", html);
        Assert.Contains("Needs Fan to be on", html, StringComparison.Ordinal);
        Assert.Contains("On start, wait", html, StringComparison.Ordinal);
        Assert.Contains("On stop, run on", html, StringComparison.Ordinal);
        Assert.Contains("TURN ON THE GROUP", html, StringComparison.Ordinal);
        Assert.Contains("The app's interlock only works while the app runs.", html, StringComparison.Ordinal);
        Assert.Contains("No contact with the relay board. The steps and their relays", html, StringComparison.Ordinal);
    }

    // Two fans in one step, two heaters in the next: one card per step on the group's page, one chip per step on its
    // card, each relay with its own ON/OFF.
    [Fact]
    public async Task Should_ShowAGroupsSteps_WithTheirRelays()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        await http.GetStringAsync("/", Cancel);
        var factory = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        int id;
        await using (var db = await factory.CreateDbContextAsync(Cancel))
        {
            foreach (var (relayId, name) in new[] { (1, "Fan 1"), (2, "Fan 2"), (3, "Heater 1"), (4, "Heater 2") })
            {
                (await db.Relays.SingleAsync(r => r.Id == relayId, Cancel)).Name = name;
            }

            var group = new RelayGroup { BoardId = 1, Name = "Workshop heat", Icon = GroupIcons.Flame, MaxOnMinutes = 120 };
            group.AddStep("Fans", [1, 2], runOnSec: 180);
            group.AddStep("Heaters", [3, 4]);
            await db.AddWithRuntimesAsync(group);
            id = group.Id;
        }

        var list = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));
        var page = WebUtility.HtmlDecode(await http.GetStringAsync($"/group/{id}", Cancel));

        Assert.Contains("GROUP · RELAY 1 + 2 → 3 + 4", list, StringComparison.Ordinal);
        Assert.Matches("class=\"chip \"[^>]*><span class=\"dot\"[^>]*></span>Fans<span class=\"visually-hidden\"[^>]*> \\(off\\)", list);
        Assert.Contains("aria-label=\"Steps in start order\"", list, StringComparison.Ordinal);
        Assert.Contains(">Heaters<", list, StringComparison.Ordinal);
        Assert.DoesNotContain(">Fan 1<", list, StringComparison.Ordinal);

        Assert.Matches("STEP 1 · NAME[\\s\\S]*value=\"Fans\"[\\s\\S]*Fan 1[\\s\\S]*Fan 2[\\s\\S]*STEP 2 · NAME[\\s\\S]*value=\"Heaters\"[\\s\\S]*Heater 1[\\s\\S]*Heater 2", page);
        Assert.Contains("Needs Fans to be on", page, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Turn on Heater 2\"", page, StringComparison.Ordinal);
        Assert.Equal(1, page.Split("On start, wait").Length - 1);
    }

    [Fact]
    public async Task Should_SendTheRelayPageToItsGroup()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        await http.GetStringAsync("/", Cancel);
        var id = await AddFanHeaterAsync(app);

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/relay/2", Cancel));

        Assert.Contains("This relay is in the group", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"group/{id}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("TURN ON", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Active</strong>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ListTheGroupsSchedules_AndOfferANewOne()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        await http.GetStringAsync("/", Cancel);
        var id = await AddFanHeaterAsync(app);
        await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel))
        {
            db.Schedules.Add(new Schedule
            {
                GroupId = id,
                Days = ScheduleDays.Weekdays,
                OnTime = new TimeOnly(6, 30),
                OffTime = new TimeOnly(8, 0),
                IsEnabled = true,
                CreatedBy = "john@example.com",
            });
            await db.SaveChangesAsync(Cancel);
        }

        var group = WebUtility.HtmlDecode(await http.GetStringAsync($"/group/{id}", Cancel));
        var form = WebUtility.HtmlDecode(await http.GetStringAsync($"/group/{id}/schedule/new", Cancel));

        Assert.Contains($"href=\"group/{id}/schedule/new\"", group, StringComparison.Ordinal);
        Assert.Contains("06:30 – 08:00", group, StringComparison.Ordinal);
        Assert.Contains("Fan heater – Lathe", form, StringComparison.Ordinal);
        Assert.Contains("the group turns on at 06:30 and off at 08:00", form, StringComparison.Ordinal);
        Assert.Contains($"href=\"group/{id}\"", form, StringComparison.Ordinal);
        Assert.Contains("The group doesn't exist.", WebUtility.HtmlDecode(await http.GetStringAsync("/group/42/schedule/new", Cancel)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_SayAMissingGroupIsMissing()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/group/42", Cancel));

        Assert.Contains("The group doesn't exist.", html, StringComparison.Ordinal);
    }

    // A board that answered and then lost contact, unless asked for one that never answered.
    private static TestWebApp CreateApp(bool answered = true)
    {
        return new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = "john@example.com" },
            answered ? TestWebApp.WithLostBoard : null);
    }

    // Straight into the database: setting up through GroupsHandler needs a board to see the relays are off.
    private static async Task<int> AddFanHeaterAsync(TestWebApp app)
    {
        var factory = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        await using var db = await factory.CreateDbContextAsync(Cancel);
        var group = new RelayGroup { BoardId = 1, Name = "Fan heater – Lathe", Icon = GroupIcons.Fan, MaxOnMinutes = 120 };
        group.AddStep("Fan", 1, runOnSec: 300);
        group.AddStep("Heat", 2);
        await db.AddWithRuntimesAsync(group);
        return group.Id;
    }
}
