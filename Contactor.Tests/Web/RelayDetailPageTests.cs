using System.Net;
using Contactor.Boards;
using Contactor.Tests.Boards;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>
/// The Relay screen as the server first renders it, with no board connected unless a test brings one.
/// </summary>
public sealed class RelayDetailPageTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ShowTheRelaysSettings()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/relay/3", Cancel));

        Assert.Contains("RELAY 3 · NAME", html, StringComparison.Ordinal);
        Assert.Contains("value=\"Relay 3\"", html, StringComparison.Ordinal);
        Assert.Contains("Off right now.", html, StringComparison.Ordinal);
        Assert.Contains("TURN ON", html, StringComparison.Ordinal);
        Assert.Matches("aria-pressed=\"true\"[^>]*>2 h</button>", html);
        Assert.Contains("Nothing has happened to this relay yet.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ShowTheRelaysLatestEvents()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        var factory = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(Cancel))
        {
            for (var i = 0; i < 6; i++)
            {
                db.EventLog.Add(new EventLogEntry { TimestampUtc = DateTime.UtcNow, RelayId = 3, Actor = "john@example.com", Action = EventActions.On });
            }

            db.EventLog.Add(new EventLogEntry { TimestampUtc = DateTime.UtcNow, RelayId = 4, Actor = "john@example.com", Action = EventActions.Off });
            await db.SaveChangesAsync(Cancel);
        }

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/relay/3", Cancel));

        Assert.Equal(4, html.Split("turned on").Length - 1);
        Assert.DoesNotContain("turned off", html, StringComparison.Ordinal);
        Assert.Contains("Today", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/relay/9")]
    [InlineData("/relay/0")]
    public async Task Should_SayTheRelayDoesNotExist(string path)
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync(path, Cancel));

        Assert.Contains("The relay doesn't exist.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_LinkEachRelayFromTheList()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        var html = await http.GetStringAsync("/", Cancel);

        for (var id = 1; id <= 8; id++)
        {
            Assert.Contains($"href=\"relay/{id}\"", html, StringComparison.Ordinal);
        }
    }

    // On a clock of the test's own, so the times on the page don't depend on the time of day (after 22:00 the auto-off
    // would be "tomorrow"), at 13:00 in the app's time zone.
    [Theory]
    [InlineData("john@example.com", "by you")]
    [InlineData(EventActors.External, "outside the app")]
    public async Task Should_SayWhoTurnedTheRelayOn(string actor, string who)
    {
        var board = new FakeRelayBoard();
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = "john@example.com" },
            services =>
            {
                TestWebApp.WithBoard(board)(services);
                services.AddSingleton<TimeProvider>(time);
            });
        using var http = app.CreateClient();
        await board.TurnOnAsync(ChannelMask.Of(3), Cancel);
        var factory = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();

        // The app notes the change it didn't make first, as made outside the app: its event and its run, saved
        // together. Only then the test's actor replaces that run, so the app's note can't overwrite it afterwards.
        await Wait.UntilAsync(() =>
        {
            using var db = factory.CreateDbContext();
            return db.EventLog.Any(e => e.RelayId == 3 && e.Action == EventActions.On && e.Actor == EventActors.External);
        });
        await using (var db = await factory.CreateDbContextAsync(Cancel))
        {
            var runtime = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 3, Cancel);
            Assert.Equal(EventActors.External, runtime.TurnedOnBy);
            (runtime.OnSinceUtc, runtime.TurnedOnBy) = (time.GetUtcNow().UtcDateTime, actor);
            await db.SaveChangesAsync(Cancel);
        }

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/relay/3", Cancel));

        Assert.Matches($"<p[^>]*>On since 13:00, {who}\\. Turns off 15:00\\.</p>", html);
    }

    private static TestWebApp CreateApp()
    {
        return new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = "john@example.com" },
            TestWebApp.WithLostBoard);
    }
}
