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

    [Theory]
    [InlineData("john@example.com", "by you")]
    [InlineData(EventActors.External, "outside the app")]
    public async Task Should_SayWhoTurnedTheRelayOn(string actor, string who)
    {
        var board = new FakeRelayBoard();
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = "john@example.com" },
            TestWebApp.WithBoard(board));
        using var http = app.CreateClient();
        await board.TurnOnAsync(ChannelMask.Of(3), Cancel);
        var factory = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();

        // The app notes the change it didn't make first, as made outside the app; the test's actor replaces that.
        await Wait.UntilAsync(() =>
        {
            using var db = factory.CreateDbContext();
            return db.RelayRuntimes.Single(r => r.RelayId == 3).TurnedOnBy is not null;
        });
        await using (var db = await factory.CreateDbContextAsync(Cancel))
        {
            var runtime = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 3, Cancel);
            (runtime.OnSinceUtc, runtime.TurnedOnBy) = (DateTime.UtcNow, actor);
            await db.SaveChangesAsync(Cancel);
        }

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/relay/3", Cancel));

        Assert.Matches($"<p[^>]*>On since [0-2][0-9]:[0-5][0-9], {who}\\.( Turns off [0-2][0-9]:[0-5][0-9]\\.)?</p>", html);
    }

    private static TestWebApp CreateApp()
    {
        return new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = "john@example.com" },
            TestWebApp.WithLostBoard);
    }
}
