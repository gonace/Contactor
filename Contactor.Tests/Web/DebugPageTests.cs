using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Contactor.Web;
using Contactor.Web.Features.Vm201;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>
/// The Debugging setting on Inställningar, the Debug tab it adds for that person, and the Debug screen.
/// </summary>
public sealed class DebugPageTests
{
    private const string Ted = "ted@example.com";
    private const string Nils = "nils@example.com";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_KeepTheSettingPerPerson()
    {
        await using var database = TestDatabase.Create((services, _) => services.AddSingleton<BoardTrafficSetting>());
        await database.Services.InitializeDatabaseAsync(Cancel);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            db.Users.AddRange(new User { Email = Ted, DisplayName = "Ted" }, new User { Email = Nils, DisplayName = "Nils" });
            await db.SaveChangesAsync(Cancel);
        }

        var setting = database.Services.GetRequiredService<BoardTrafficSetting>();
        List<string> changed = [];
        setting.Changed += (_, email) => changed.Add(email);

        await setting.SetAsync(Ted, true, Cancel);
        await setting.SetAsync(Ted, true, Cancel);

        Assert.True(await setting.IsOnAsync(Ted, Cancel));
        Assert.False(await setting.IsOnAsync(Nils, Cancel));
        Assert.False(await setting.IsOnAsync("nobody@example.com", Cancel));
        Assert.Equal([Ted], changed);

        await setting.SetAsync(Ted, false, Cancel);
        Assert.False(await setting.IsOnAsync(Ted, Cancel));
    }

    [Fact]
    public async Task Should_ShowTheDebugTab_OnlyWhileDebuggingIsOn()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = Ted });
        using var http = app.CreateClient();

        var before = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));
        Assert.Contains("Visa trafiken till reläkortet", before, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"debug\"", before, StringComparison.Ordinal);

        await app.Services.GetRequiredService<BoardTrafficSetting>().SetAsync(Ted, true, Cancel);

        foreach (var path in new[] { "/", "/handelser", "/settings" })
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
            new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = Ted });
        using var http = app.CreateClient();
        await http.GetStringAsync("/", Cancel);
        var trace = app.Services.GetRequiredService<BoardTrace>();

        // Nothing listens on the test board's port, so the client's own attempts show up.
        await WaitUntilAsync(() => trace.Items.Any(i => i.Kind == Contactor.Vm201.Vm201TraceKind.ConnectionLost));
        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/debug", Cancel));

        Assert.Matches("<h1[^>]*>Debug</h1>", html);
        Assert.Contains("Ansluter till 127.0.0.1:1", html, StringComparison.Ordinal);
        Assert.Contains("Anslutningen bröts", html, StringComparison.Ordinal);
        Assert.Contains("href=\"debug\" aria-current=\"page\"", html, StringComparison.Ordinal);
        Assert.True(
            html.IndexOf("Anslutningen bröts", StringComparison.Ordinal) < html.LastIndexOf("Ansluter till", StringComparison.Ordinal),
            "The newest step comes first.");
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
