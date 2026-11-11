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

    [Fact]
    public async Task Should_ShowTheGroupsRelaysInOrder_WithTheInterlock()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        await http.GetStringAsync("/", Cancel);
        var id = await AddFanHeaterAsync(app);

        var html = WebUtility.HtmlDecode(await http.GetStringAsync($"/group/{id}", Cancel));

        Assert.Contains("Relays in start order", html, StringComparison.Ordinal);
        Assert.Matches("type=\"radio\"[^>]*value=\"fan\" checked", html);
        Assert.Matches("value=\"Fan\"[\\s\\S]*value=\"Heat\"", html);
        Assert.Contains("Needs Fan to be on", html, StringComparison.Ordinal);
        Assert.Contains("On start, wait", html, StringComparison.Ordinal);
        Assert.Contains("On stop, run on", html, StringComparison.Ordinal);
        Assert.Contains("TURN ON THE GROUP", html, StringComparison.Ordinal);
        Assert.Contains("The app's interlock only works while the app runs.", html, StringComparison.Ordinal);
        Assert.Contains("No contact with the relay board. The relays and their order", html, StringComparison.Ordinal);
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
        Assert.DoesNotContain("Aktiv</strong>", html, StringComparison.Ordinal);
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

    private static TestWebApp CreateApp()
    {
        return new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "john@example.com" });
    }

    // Straight into the database: setting up through GroupsHandler needs a board to see the relays are off.
    private static async Task<int> AddFanHeaterAsync(TestWebApp app)
    {
        var factory = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        await using var db = await factory.CreateDbContextAsync(Cancel);
        var group = new RelayGroup { Name = "Fan heater – Lathe", Icon = GroupIcons.Fan, MaxOnMinutes = 120 };
        group.Members.Add(new RelayGroupMember { RelayId = 1, Position = 1, RoleName = "Fan", RunOnSec = 300 });
        group.Members.Add(new RelayGroupMember { RelayId = 2, Position = 2, RoleName = "Heat" });
        db.RelayGroups.Add(group);
        await db.SaveChangesAsync(Cancel);
        db.GroupRuntimes.Add(new GroupRuntime { GroupId = group.Id });
        await db.SaveChangesAsync(Cancel);
        return group.Id;
    }
}
