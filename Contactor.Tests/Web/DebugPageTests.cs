using System.Net;
using Contactor.Web;
using Contactor.Web.Features.Boards;
using Contactor.Web.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// The Debugging setting on Settings, the Debug tab it adds for that person, and the Debug screen.
/// </summary>
public sealed class DebugPageTests
{
    private const string John = "john@example.com";
    private const string Jane = "jane@example.com";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_KeepTheSettingPerPerson()
    {
        await using var database = TestDatabase.Create((services, _) => services.AddSingleton<BoardTrafficSetting>());
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            db.Users.AddRange(new User { Email = John, DisplayName = "John" }, new User { Email = Jane, DisplayName = "Jane" });
            await db.SaveChangesAsync(Cancel);
        }

        var setting = database.Services.GetRequiredService<BoardTrafficSetting>();
        List<string> changed = [];
        setting.Changed += (_, email) => changed.Add(email);

        await setting.SetAsync(John, true, Cancel);
        await setting.SetAsync(John, true, Cancel);

        Assert.True(await setting.IsOnAsync(John, Cancel));
        Assert.False(await setting.IsOnAsync(Jane, Cancel));
        Assert.False(await setting.IsOnAsync("nobody@example.com", Cancel));
        Assert.Equal([John], changed);

        await setting.SetAsync(John, false, Cancel);
        Assert.False(await setting.IsOnAsync(John, Cancel));
    }

    [Fact]
    public async Task Should_ShowTheDebugTab_OnlyWhileDebuggingIsOn()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = John });
        using var http = app.CreateClient();

        var before = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));
        Assert.Contains("Show the traffic to the relay board", before, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"debug\"", before, StringComparison.Ordinal);

        await app.Services.GetRequiredService<BoardTrafficSetting>().SetAsync(John, true, Cancel);

        foreach (var path in new[] { "/", "/events", "/settings" })
        {
            var html = WebUtility.HtmlDecode(await http.GetStringAsync(path, Cancel));
            Assert.Contains("href=\"debug\"", html, StringComparison.Ordinal);
        }

        var settings = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));
        Assert.Matches("id=\"debugging\"[^>]*checked", settings);
    }

    [Fact]
    public async Task Should_ListTheBoardTrafficNewestFirst()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = John });
        using var http = app.CreateClient();
        await http.GetStringAsync("/", Cancel);
        var trace = app.Services.GetRequiredService<BoardTrace>();

        // Nothing listens on the test board's port, so the client's own attempts show up.
        await WaitUntilAsync(() => trace.Items.Any(i => i.Text.StartsWith("Connection lost", StringComparison.Ordinal)));
        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/debug", Cancel));

        Assert.Matches("<h1[^>]*>Debug</h1>", html);
        Assert.Contains("Connecting to 127.0.0.1:1", html, StringComparison.Ordinal);
        Assert.Contains("Connection lost", html, StringComparison.Ordinal);
        Assert.Contains("href=\"debug\" aria-current=\"page\"", html, StringComparison.Ordinal);
        Assert.True(
            html.IndexOf("Connection lost", StringComparison.Ordinal) < html.LastIndexOf("Connecting to", StringComparison.Ordinal),
            "The newest step comes first.");
        Assert.DoesNotContain("routine", html, StringComparison.Ordinal);

        trace.Record(new Contactor.Boards.BoardTraceEntry(DateTimeOffset.UnixEpoch, Contactor.Boards.BoardTraceDirection.Sent, "Ask for status", null, false, IsRoutine: true));
        trace.Record(new Contactor.Boards.BoardTraceEntry(DateTimeOffset.UnixEpoch, Contactor.Boards.BoardTraceDirection.Sent, "Ask for status", null, false, IsRoutine: true));
        html = WebUtility.HtmlDecode(await http.GetStringAsync("/debug", Cancel));
        Assert.Contains("2 routine steps left out: status requests and answers that changed nothing.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Ask for status", html, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The condition was not met in time.");
            }

            await Task.Delay(20, Cancel);
        }
    }
}
