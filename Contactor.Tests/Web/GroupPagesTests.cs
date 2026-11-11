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

        Assert.Contains("GRUPP · RELÄ 1 → 2", html, StringComparison.Ordinal);
        Assert.Contains("Värmefläkt – Svarven", html, StringComparison.Ordinal);
        Assert.Matches("<span class=\"chip[^\"]*\"[^>]*>.*Fläkt", html);
        Assert.Contains("aria-label=\"Slå på Värmefläkt – Svarven\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-label=\"Slå på Relä 1\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-label=\"Slå på Relä 2\"", html, StringComparison.Ordinal);
        Assert.Contains("0 av 7 på", html, StringComparison.Ordinal);
        Assert.Contains("<svg class=\"group-icon\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ShowTheGroupsRelaysInOrder_WithTheInterlock()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        await http.GetStringAsync("/", Cancel);
        var id = await AddFanHeaterAsync(app);

        var html = WebUtility.HtmlDecode(await http.GetStringAsync($"/grupp/{id}", Cancel));

        Assert.Contains("Reläer i startordning", html, StringComparison.Ordinal);
        Assert.Matches("type=\"radio\"[^>]*value=\"fan\" checked", html);
        Assert.Matches("value=\"Fläkt\"[\\s\\S]*value=\"Värme\"", html);
        Assert.Contains("Kräver att Fläkt är på", html, StringComparison.Ordinal);
        Assert.Contains("Vid start, vänta", html, StringComparison.Ordinal);
        Assert.Contains("Vid stopp, eftergång", html, StringComparison.Ordinal);
        Assert.Contains("SLÅ PÅ GRUPPEN", html, StringComparison.Ordinal);
        Assert.Contains("Spärren i appen fungerar bara när appen kör.", html, StringComparison.Ordinal);
        Assert.Contains("Ingen kontakt med reläkortet. Vilka reläer som ingår", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_SendTheRelayPageToItsGroup()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        await http.GetStringAsync("/", Cancel);
        var id = await AddFanHeaterAsync(app);

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/rele/2", Cancel));

        Assert.Contains("Reläet ingår i gruppen", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"grupp/{id}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("SLÅ PÅ", html, StringComparison.Ordinal);
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
                CreatedBy = "ted@example.com",
            });
            await db.SaveChangesAsync(Cancel);
        }

        var group = WebUtility.HtmlDecode(await http.GetStringAsync($"/grupp/{id}", Cancel));
        var form = WebUtility.HtmlDecode(await http.GetStringAsync($"/grupp/{id}/schema/ny", Cancel));

        Assert.Contains($"href=\"grupp/{id}/schema/ny\"", group, StringComparison.Ordinal);
        Assert.Contains("06:30 – 08:00", group, StringComparison.Ordinal);
        Assert.Contains("Värmefläkt – Svarven", form, StringComparison.Ordinal);
        Assert.Contains("gruppen slås på 06:30 och stängs av 08:00", form, StringComparison.Ordinal);
        Assert.Contains($"href=\"grupp/{id}\"", form, StringComparison.Ordinal);
        Assert.Contains("Gruppen finns inte.", WebUtility.HtmlDecode(await http.GetStringAsync("/grupp/42/schema/ny", Cancel)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_SayAMissingGroupIsMissing()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/grupp/42", Cancel));

        Assert.Contains("Gruppen finns inte.", html, StringComparison.Ordinal);
    }

    private static TestWebApp CreateApp()
    {
        return new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "ted@example.com" });
    }

    // Straight into the database: setting up through GroupsHandler needs a board to see the relays are off.
    private static async Task<int> AddFanHeaterAsync(TestWebApp app)
    {
        var factory = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        await using var db = await factory.CreateDbContextAsync(Cancel);
        var group = new RelayGroup { Name = "Värmefläkt – Svarven", Icon = GroupIcons.Fan, MaxOnMinutes = 120 };
        group.Members.Add(new RelayGroupMember { RelayId = 1, Position = 1, RoleName = "Fläkt", RunOnSec = 300 });
        group.Members.Add(new RelayGroupMember { RelayId = 2, Position = 2, RoleName = "Värme" });
        db.RelayGroups.Add(group);
        await db.SaveChangesAsync(Cancel);
        db.GroupRuntimes.Add(new GroupRuntime { GroupId = group.Id });
        await db.SaveChangesAsync(Cancel);
        return group.Id;
    }
}
