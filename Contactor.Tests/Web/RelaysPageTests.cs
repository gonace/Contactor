using System.Net;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// The relay screen as the server first renders it, with no board connected.
/// </summary>
public sealed class RelaysPageTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ListActiveRelaysAndGreetTheUser()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "john@example.com" },
            TestWebApp.WithLostBoard);
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        Assert.Contains("Hi John", html, StringComparison.Ordinal);
        Assert.Contains("0 of 8 on", html, StringComparison.Ordinal);
        for (var id = 1; id <= 8; id++)
        {
            Assert.Contains($"Relay {id}", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Should_ShowNoSectionHeadings_WithOnlyRelays()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "john@example.com" });

        // The board's one input hidden: only relays are left, so they need no heading.
        await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel))
        {
            (await db.Inputs.SingleAsync(i => i.Id == 1, Cancel)).IsActive = false;
            await db.SaveChangesAsync(Cancel);
        }

        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        Assert.DoesNotContain("id=\"groups-heading\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"relays-heading\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"inputs-heading\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-labelledby", html, StringComparison.Ordinal);
    }

    // With every relay in a group there is no relay card, so no "Relays" heading and no empty grid.
    [Fact]
    public async Task Should_ShowNoRelaysHeading_WhenEveryRelayIsInAGroup()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "john@example.com" });
        await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel))
        {
            for (var g = 0; g < 4; g++)
            {
                var group = new RelayGroup { Name = $"Group {g + 1}", Icon = GroupIcons.Fan, MaxOnMinutes = 120 };
                group.Members.Add(new RelayGroupMember { RelayId = (g * 2) + 1, Position = 1, RoleName = "Fan" });
                group.Members.Add(new RelayGroupMember { RelayId = (g * 2) + 2, Position = 2, RoleName = "Heat" });
                db.RelayGroups.Add(group);
            }

            await db.SaveChangesAsync(Cancel);
            db.GroupRuntimes.AddRange(db.RelayGroups.Select(g => new GroupRuntime { GroupId = g.Id }));
            await db.SaveChangesAsync(Cancel);
        }

        using var http = app.CreateClient();
        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        Assert.Contains("id=\"groups-heading\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"relays-heading\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-labelledby=\"relays-heading\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("relay-cards", html, StringComparison.Ordinal);
    }

    // One group and every other relay inactive: no relay card shows, so no empty grid, only "Show N inactive relays".
    [Fact]
    public async Task Should_ShowNoRelaysSection_WhenTheOtherRelaysAreInactive()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "john@example.com" });
        await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel))
        {
            var group = new RelayGroup { Name = "Fan heater", Icon = GroupIcons.Fan, MaxOnMinutes = 120 };
            group.Members.Add(new RelayGroupMember { RelayId = 1, Position = 1, RoleName = "Fan" });
            group.Members.Add(new RelayGroupMember { RelayId = 2, Position = 2, RoleName = "Heat" });
            db.RelayGroups.Add(group);
            await db.Relays.Where(r => r.Id > 2).ExecuteUpdateAsync(r => r.SetProperty(x => x.IsActive, false), Cancel);
            await db.SaveChangesAsync(Cancel);
            db.GroupRuntimes.Add(new GroupRuntime { GroupId = group.Id });
            await db.SaveChangesAsync(Cancel);
        }

        using var http = app.CreateClient();
        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        Assert.Contains("Fan heater", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"relays-heading\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("relay-cards", html, StringComparison.Ordinal);
        Assert.Contains("Show 6 inactive relays", html, StringComparison.Ordinal);
    }

    // Groups first and relays after, each in a section of its own, so the cards line up and Tab goes row by row.
    [Fact]
    public async Task Should_ListTheGroupsFirst_InASectionOfTheirOwn()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "john@example.com" });
        await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel))
        {
            var group = new RelayGroup { Name = "Fan heater", Icon = GroupIcons.Fan, MaxOnMinutes = 120 };
            group.Members.Add(new RelayGroupMember { RelayId = 1, Position = 1, RoleName = "Fan" });
            group.Members.Add(new RelayGroupMember { RelayId = 2, Position = 2, RoleName = "Heat" });
            db.RelayGroups.Add(group);
            await db.SaveChangesAsync(Cancel);
            db.GroupRuntimes.Add(new GroupRuntime { GroupId = group.Id });
            await db.SaveChangesAsync(Cancel);
        }

        using var http = app.CreateClient();
        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        var groups = html.IndexOf("id=\"groups-heading\"", StringComparison.Ordinal);
        var card = html.IndexOf("Fan heater", StringComparison.Ordinal);
        var relays = html.IndexOf("id=\"relays-heading\"", StringComparison.Ordinal);
        var relay = html.IndexOf("Relay 3", StringComparison.Ordinal);
        Assert.True(groups >= 0 && groups < card && card < relays && relays < relay, $"{groups} {card} {relays} {relay}");
    }

    [Fact]
    public async Task Should_WarnAndDisableButtons_WhenTheBoardIsNotConnected()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "john@example.com" },
            TestWebApp.WithLostBoard);
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        Assert.Contains("No contact with the relay board", html, StringComparison.Ordinal);
        Assert.Contains("The states below may be wrong.", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Turn on Relay 1\"", html, StringComparison.Ordinal);
        Assert.Equal(9, CountOccurrences(html, "disabled"));
    }

    // Before the board's first answer nothing is known: every card is grey with a "–", and nothing counts as on.
    [Fact]
    public async Task Should_ShowEveryStateAsUnknown_BeforeTheBoardsFirstAnswer()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "john@example.com" });
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        Assert.Contains("No contact with the relay board yet", html, StringComparison.Ordinal);
        Assert.Contains("Waiting for its first answer.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("The states below may be wrong.", html, StringComparison.Ordinal);
        Assert.DoesNotContain(" on</strong>", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">OFF<", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">Open<", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Relay 1: Waiting for the board\"", html, StringComparison.Ordinal);
        Assert.Equal(8, CountOccurrences(html, "class=\"toggle unknown\""));
        Assert.Equal(1, CountOccurrences(html, "class=\"state unknown\""));

        // The same on the screens of a relay and an input.
        var relay = WebUtility.HtmlDecode(await http.GetStringAsync("/relay/3", Cancel));
        var input = WebUtility.HtmlDecode(await http.GetStringAsync("/input/1", Cancel));
        Assert.Contains("<section class=\"status unknown\"", relay, StringComparison.Ordinal);
        Assert.DoesNotContain("TURN ON", relay, StringComparison.Ordinal);
        Assert.Contains("Waiting for the board", input, StringComparison.Ordinal);
        Assert.Contains("class=\"state unknown\"", input, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ServeTheFonts()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "john@example.com" });
        using var http = app.CreateClient();

        using var response = await http.GetAsync("/fonts/ibm-plex-sans-latin-400-normal.woff2", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

}
