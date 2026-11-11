using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>
/// The Händelser screen and the relay screen's off line, as the server first renders them.
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
            Actor = "ted@example.com",
            Action = EventActions.On,
        });

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/handelser", Cancel));

        Assert.Matches("<h1[^>]*>Händelser</h1>", html);
        Assert.Contains("IDAG", html, StringComparison.Ordinal);
        Assert.Matches("<strong[^>]*>Ted</strong> slog på", html);
        Assert.Contains("Relä 2", html, StringComparison.Ordinal);
        foreach (var filter in new[] { "Alla", "Personer", "Automatiskt" })
        {
            Assert.Contains($">{filter}</button>", html, StringComparison.Ordinal); // The attributes come before ">".
        }

        Assert.DoesNotContain("Visa fler", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_OfferMore_WhenThereAreMoreThanAPage()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        for (var i = 0; i < 51; i++)
        {
            await AddEventAsync(app, new EventLogEntry { TimestampUtc = DateTime.UtcNow, RelayId = 1, Actor = "ted@example.com", Action = EventActions.On });
        }

        var html = await http.GetStringAsync("/handelser", Cancel);

        Assert.Contains("Visa fler", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_SayNothingHappenedYet_WhenTheLogIsEmpty()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/handelser", Cancel));

        Assert.Contains("Inga händelser än.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Utvecklingsversion", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ShowWhoTurnedARelayOff_OnTheRelayScreen()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        await AddEventAsync(app, new EventLogEntry { TimestampUtc = DateTime.UtcNow.AddMinutes(-1), RelayId = 3, Actor = "lars@example.com", Action = EventActions.Off });
        await AddEventAsync(app, new EventLogEntry { TimestampUtc = DateTime.UtcNow, RelayId = 4, Actor = EventActors.Schedule, Action = EventActions.Off });

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        Assert.Matches(@"Av sedan \d\d:\d\d · Lars", html);
        Assert.Matches(@"Av sedan \d\d:\d\d · schema", html);
    }

    private static TestWebApp CreateApp()
    {
        return new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = "ted@example.com" });
    }

    private static async Task AddEventAsync(TestWebApp app, EventLogEntry entry)
    {
        var factory = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        await using var db = await factory.CreateDbContextAsync(Cancel);
        db.EventLog.Add(entry);
        await db.SaveChangesAsync(Cancel);
    }
}
