using System.Net;
using Contactor.Web.Components;
using Contactor.Web.Features.Updates;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>Settings → Updates, the splash and the update's log, in the running app.</summary>
public sealed class UpdatePageTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static readonly Release Newest = new("2026.11.20", new DateOnly(2026, 11, 20), ["Several relay boards in one app", "Events and Debug name the board"]);
    private static readonly Release Between = new("2026.11.17", new DateOnly(2026, 11, 17), ["The test run prints only its own lines"]);

    [Fact]
    public async Task Should_OfferTheNewVersion_WithWhatsNew()
    {
        await using var app = App();
        await StartAsync(app, helper: true);
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));

        Assert.Matches("You have version <strong[^>]*>2026.11.14</strong>.", html);
        Assert.Contains("Version 2026.11.20 is available", html, StringComparison.Ordinal);
        Assert.Contains("What's new", html, StringComparison.Ordinal);
        Assert.Matches("<li[^>]*>Several relay boards in one app</li>", html);
        Assert.Matches("<li[^>]*>The test run prints only its own lines</li>", html);
        Assert.Contains("20 November", html, StringComparison.Ordinal);
        Assert.Matches(">\\s*Update now\\s*</button>", html);
        Assert.Contains("Contactor restarts. That takes about a minute. The relays stay as they are, and schedules catch up afterwards.", html, StringComparison.Ordinal);
    }

    // A template or a plain compose run: the platform updates Contactor.
    [Fact]
    public async Task Should_SayWhereUpdatesComeFrom_WithoutTheHelper()
    {
        await using var app = App();
        await StartAsync(app, helper: false);
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));

        Assert.Contains("Version 2026.11.20 is available", html, StringComparison.Ordinal);
        Assert.DoesNotMatch(">\\s*Update now\\s*</button>", html);
        Assert.Contains("Contactor is updated where it was installed", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_AskFirst_ThenStartTheUpdate()
    {
        await using var app = App();
        var service = await StartAsync(app, helper: true);
        using var page = await PageRenderer.StartAsync<UpdateBox>(app.Services, "john@example.com", "John");

        await page.ClickAsync(PageRenderer.WithText("Update now"));
        var asking = await page.TextAsync();
        Assert.Contains("Update to 2026.11.20?", asking, StringComparison.Ordinal);
        Assert.Contains("Contactor restarts. That takes about a minute, and the app doesn't answer meanwhile.", asking, StringComparison.Ordinal);
        Assert.Contains("The relays stay as they are. Schedules and auto-offs catch up once it's back.", asking, StringComparison.Ordinal);
        Assert.False(File.Exists(service.Folder.RequestPath));

        await page.ClickAsync(PageRenderer.WithText("Update"));

        Assert.Equal("update\n", await File.ReadAllTextAsync(service.Folder.RequestPath, Cancel));
        var text = await page.TextAsync();
        Assert.Contains("Updating from 2026.11.14 to 2026.11.20.", text, StringComparison.Ordinal);
        Assert.Contains("Downloading 2026.11.20…", text, StringComparison.Ordinal);
        Assert.Contains("Started by John at ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Update to 2026.11.20?", text, StringComparison.Ordinal);
        Assert.Empty(page.Errors);
    }

    [Fact]
    public async Task Should_NotUpdate_WhenTheQuestionIsCancelled()
    {
        await using var app = App();
        var service = await StartAsync(app, helper: true);
        using var page = await PageRenderer.StartAsync<UpdateBox>(app.Services, "john@example.com", "John");

        await page.ClickAsync(PageRenderer.WithText("Update now"));
        await page.ClickAsync(PageRenderer.WithText("Cancel"));

        Assert.DoesNotContain("Update to 2026.11.20?", await page.TextAsync(), StringComparison.Ordinal);
        Assert.False(File.Exists(service.Folder.RequestPath));
    }

    [Fact]
    public async Task Should_ShowWhatHappened_WithSecretsStripped()
    {
        await using var app = App();
        var service = await StartAsync(app, helper: true);
        Directory.CreateDirectory(service.Folder.LogsFolder);
        await File.WriteAllTextAsync(Path.Combine(service.Folder.LogsFolder, "update-20261112-095800.log"),
            "10:00:01 Downloading.\n    starting with Tunnel__Token=eyJhIjoiMTIzIn0.c2VjcmV0\n10:00:02 Couldn't download.\n", Cancel);
        WriteStatus(service, "state=failed\nreason=download");
        using var page = await PageRenderer.StartAsync<UpdateBox>(app.Services, "john@example.com", "John");

        var text = await page.TextAsync();
        Assert.Contains("Couldn't download 2026.11.20", text, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed. Check the internet connection and try again.", text, StringComparison.Ordinal);
        Assert.True(await page.HasAsync(PageRenderer.WithText("Try again")));

        await page.ClickAsync(PageRenderer.WithText("Show what happened"));

        text = await page.TextAsync();
        Assert.Contains("10:00:02 Couldn't download.", text, StringComparison.Ordinal);
        Assert.Contains("Tunnel__Token=[hidden]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhIjoi", text, StringComparison.Ordinal);
        Assert.True(await page.HasAsync(PageRenderer.With("href", "settings/update-log?id=20261112-095800")));
    }

    [Fact]
    public async Task Should_DownloadTheLog_AsAStrippedTextFile()
    {
        await using var app = App();
        var service = await StartAsync(app, helper: true);
        Directory.CreateDirectory(service.Folder.LogsFolder);
        await File.WriteAllTextAsync(Path.Combine(service.Folder.LogsFolder, "update-20261112-095800.log"), "10:00 password=hunter2\n", Cancel);
        using var http = app.CreateClient();

        using var response = await http.GetAsync("/settings/update-log?id=20261112-095800", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("contactor-update-20261112-095800.txt", response.Content.Headers.ContentDisposition!.FileName);
        Assert.Equal("10:00 password=[hidden]\n", await response.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/settings/update-log?id=..%2Fstatus", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/settings/update-log?id=20261112-095801", Cancel)).StatusCode);
    }

    [Fact]
    public async Task Should_ShowTheSplash_OnEveryPage_WhileTheAppRestarts()
    {
        await using var app = App();
        var service = await StartAsync(app, helper: true);
        using var page = await PageRenderer.StartAsync<UpdateSplash>(app.Services, "jane@example.com", "Jane");
        Assert.DoesNotContain("Contactor is updating", await page.TextAsync(), StringComparison.Ordinal);
        var renders = page.Renders;

        WriteStatus(service, "state=running\nstep=restart");
        await WaitForAsync(() => page.Renders > renders);

        var text = await page.TextAsync();
        Assert.Contains("Contactor is updating", text, StringComparison.Ordinal);
        Assert.Contains("To version 2026.11.20. This takes about a minute, and the page comes back by itself.", text, StringComparison.Ordinal);
        Assert.Contains("The relays stay as they are. Schedules and auto-offs catch up once it's back.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_SayItIsTheNewest_InADevelopmentBuild()
    {
        await using var app = new TestWebApp("Development", new Dictionary<string, string?> { [TestWebApp.SignedInAs] = "john@example.com" });
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));

        Assert.Contains("This is a development version of Contactor. It isn't updated from here.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Update now", html, StringComparison.Ordinal);
    }

    private static TestWebApp App()
    {
        return new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = "john@example.com" },
            TestWebApp.WithReleases("2026.11.14", Newest, Between));
    }

    // Starts the app, says whether the helper is there, and reads the releases, so the page has them at once.
    private static async Task<UpdateService> StartAsync(TestWebApp app, bool helper)
    {
        var service = app.Services.GetRequiredService<UpdateService>();
        Directory.CreateDirectory(service.Folder.Path);
        if (helper)
        {
            await File.WriteAllTextAsync(service.Folder.HelperPath, "version=1\n", Cancel);
        }

        await service.CheckAsync(Cancel);
        service.Poll();
        return service;
    }

    // The helper's status for update 20261112-095800, from 2026.11.14 to 2026.11.20, as just written.
    private static void WriteStatus(UpdateService service, string lines)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(service.Folder.StatusPath, $"helper=1\nid=20261112-095800\nfrom=2026.11.14\nto=2026.11.20\nstarted={now}\nchanged={now}\n{lines}\n");
        File.SetLastWriteTimeUtc(service.Folder.StatusPath, DateTime.UtcNow.AddMinutes(1));
        if (service.Poll())
        {
            service.FollowStatusAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    // The page redraws on the service's own thread; this waits for it by counting, never against the clock alone.
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, Cancel);
        }

        Assert.True(condition());
    }
}
