using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Contactor.Web.Features.Events;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>
/// The Events screen and the relay screen's off line, as the server first renders them.
/// </summary>
public sealed class EventsPageTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ShowEventsWithTheirFilters()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        await AddEventAsync(app, new EventLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            RelayId = 2,
            Actor = "john@example.com",
            Action = EventActions.On,
        });

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/events", Cancel));

        Assert.Matches("<h1[^>]*>Events</h1>", html);
        Assert.Contains("TODAY", html, StringComparison.Ordinal);
        Assert.Matches("<strong[^>]*>John</strong> turned on", html);
        Assert.Contains("Relay 2", html, StringComparison.Ordinal);
        foreach (var filter in new[] { "All", "People", "Automatic" })
        {
            Assert.Contains($">{filter}</button>", html, StringComparison.Ordinal); // The attributes come before ">".
        }

        Assert.DoesNotContain("Show more", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_SayTheTimeIsUnknown_ForAnInputChangeNoticedAfterAGap()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        await AddEventAsync(app, new EventLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            InputId = 1,
            Actor = EventActors.External,
            Action = EventActions.InputChanged,
            Details = """{"on":false,"noticed":true}""",
        });
        await AddEventAsync(app, new EventLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            InputId = 1,
            Actor = EventActors.External,
            Action = EventActions.InputChanged,
            Details = """{"on":true}""",
        });

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/events", Cancel));

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "· time unknown"));
        Assert.Matches(@"Open<span[^>]*> · time unknown</span>", html);
        Assert.Contains(EventText.WhyTimeUnknown, html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Why is the time unknown?\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_OfferMore_WhenThereAreMoreThanAPage()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        for (var i = 0; i < 51; i++)
        {
            await AddEventAsync(app, new EventLogEntry { TimestampUtc = DateTime.UtcNow, RelayId = 1, Actor = "john@example.com", Action = EventActions.On });
        }

        var html = await http.GetStringAsync("/events", Cancel);

        Assert.Contains("Show more", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_SayNothingHappenedYet_WhenTheLogIsEmpty()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/events", Cancel));

        Assert.Contains("No events yet.", html, StringComparison.Ordinal);
        // The version shows only in the desktop's side menu here, never in the page itself.
        var version = html.IndexOf("Development version", StringComparison.Ordinal);
        Assert.InRange(version, html.IndexOf("<nav class=\"tabs\"", StringComparison.Ordinal), html.IndexOf("</nav>", StringComparison.Ordinal));
        Assert.Equal(version, html.LastIndexOf("Development version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_ShowWhoTurnedARelayOff_OnTheRelayScreen()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        await AddEventAsync(app, new EventLogEntry { TimestampUtc = DateTime.UtcNow.AddMinutes(-1), RelayId = 3, Actor = "alex@example.com", Action = EventActions.Off });
        await AddEventAsync(app, new EventLogEntry { TimestampUtc = DateTime.UtcNow, RelayId = 4, Actor = EventActors.Schedule, Action = EventActions.Off });

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        Assert.Matches(@"Off since \d\d:\d\d · Alex", html);
        Assert.Matches(@"Off since \d\d:\d\d · schedule", html);
    }

    private static TestWebApp CreateApp()
    {
        return new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "john@example.com" },
            TestWebApp.WithLostBoard);
    }

    private static async Task AddEventAsync(TestWebApp app, EventLogEntry entry)
    {
        var factory = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        await using var db = await factory.CreateDbContextAsync(Cancel);
        db.EventLog.Add(entry);
        await db.SaveChangesAsync(Cancel);
    }
}
