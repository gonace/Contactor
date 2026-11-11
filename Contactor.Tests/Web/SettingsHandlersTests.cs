using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Contactor.Web.Features.Backup;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Identity;
using Contactor.Web.Features.Vm201;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>
/// What the Settings screen saves: the board's login, people's names and the backup download.
/// </summary>
public sealed class SettingsHandlersTests
{
    private const string John = "john@example.com";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_SaveTheLoginAndLogOnlyThatItChanged()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var board = rig.Database.Services.GetRequiredService<BoardAddressHandler>();

        Assert.Equal(new BoardLoginView("", false), await board.GetLoginAsync(Cancel));
        Assert.Null(await board.SaveLoginAsync("admin", "secret", none: false, John, Cancel));

        Assert.Equal(new BoardLoginView("admin", true), await board.GetLoginAsync(Cancel));
        Assert.Equal(("admin", "secret"), (rig.Client.Login.Username, rig.Client.Login.Password));
        var entry = Assert.Single(await rig.EventsAsync(), e => e.Action == EventActions.BoardLoginChanged);
        Assert.Null(entry.Details);
        Assert.Equal("changed the relay board's login", EventText.For(entry, null, "John").What);
    }

    [Fact]
    public async Task Should_KeepThePassword_WhenLeftEmpty()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var board = rig.Database.Services.GetRequiredService<BoardAddressHandler>();
        await board.SaveLoginAsync("admin", "secret", none: false, John, Cancel);

        Assert.Null(await board.SaveLoginAsync("boss", "", none: false, John, Cancel));

        Assert.Equal(("boss", "secret"), (rig.Client.Login.Username, rig.Client.Login.Password));
    }

    [Fact]
    public async Task Should_RemoveTheLogin_WhenTheBoardHasNone()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var board = rig.Database.Services.GetRequiredService<BoardAddressHandler>();
        await board.SaveLoginAsync("admin", "secret", none: false, John, Cancel);

        Assert.Null(await board.SaveLoginAsync("admin", "ignored", none: true, John, Cancel));

        Assert.True(rig.Client.Login.IsNone);
        Assert.Equal(new BoardLoginView("", false), await board.GetLoginAsync(Cancel));
    }

    [Fact]
    public async Task Should_SaveNoLogin_TheBoardCannotTake()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var board = rig.Database.Services.GetRequiredService<BoardAddressHandler>();

        Assert.NotNull(await board.SaveLoginAsync("admin", "far too long", none: false, John, Cancel));

        Assert.True(rig.Client.Login.IsNone);
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        Assert.Empty(await db.BoardSettings.ToListAsync(Cancel));
    }

    [Fact]
    public async Task Should_UseTheSavedLogin_AfterARestart()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            db.BoardSettings.Add(new BoardSetting { Host = "127.0.0.1", Port = rig.Simulator.Port, Username = "admin", Password = "secret" });
            await db.SaveChangesAsync(Cancel);
        }

        await rig.Database.Services.GetRequiredService<BoardAddressHandler>().ApplySavedAsync(Cancel);

        Assert.Equal(("admin", "secret"), (rig.Client.Login.Username, rig.Client.Login.Password));
    }

    [Fact]
    public async Task Should_RenamePeople()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        await http.GetAsync("/", Cancel);
        var users = app.Services.GetRequiredService<UsersHandler>();

        Assert.Null(await users.RenameAsync(John, "  John E. ", Cancel));
        Assert.Equal("Enter a name.", await users.RenameAsync(John, " ", Cancel));
        Assert.Equal("The name can be at most 40 characters.", await users.RenameAsync(John, new string('a', 41), Cancel));
        Assert.Equal("That person no longer exists.", await users.RenameAsync("nobody@example.com", "X", Cancel));

        Assert.Equal([new UserView(John, "John E.")], await users.ListAsync(Cancel));
        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));
        Assert.Contains("Hi John E.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_DownloadTheNewestBackup()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();

        using (var none = await http.GetAsync(BackupEndpoints.LatestPath, Cancel))
        {
            Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        }

        await app.Services.GetRequiredService<BackupService>().RunAsync(Cancel);
        using var response = await http.GetAsync(BackupEndpoints.LatestPath, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        var latest = (await app.Services.GetRequiredService<BackupService>().LatestAsync(Cancel))!;
        var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.Equal(Path.ChangeExtension(latest.Name, ".zip"), fileName);
        Assert.Matches(@"^contactor-\d{4}-\d{2}-\d{2}\.zip$", fileName);

        var bytes = await response.Content.ReadAsByteArrayAsync(Cancel);
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
        var entry = Assert.Single(zip.Entries);
        Assert.Equal(latest.Name, entry.FullName);
        using var unpacked = new MemoryStream();
        await using (var stream = await entry.OpenAsync(Cancel))
        {
            await stream.CopyToAsync(unpacked, Cancel);
        }

        Assert.Equal(await File.ReadAllBytesAsync(latest.FullName, Cancel), unpacked.ToArray());
        Assert.True(bytes.Length < unpacked.Length, "The zip is smaller than the database.");
        Assert.Empty(Directory.GetFiles(latest.DirectoryName!, ".download-*"));
    }

    [Fact]
    public async Task Should_RemoveAnOldLeftoverDownload()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        await app.Services.GetRequiredService<BackupService>().RunAsync(Cancel);
        var folder = (await app.Services.GetRequiredService<BackupService>().LatestAsync(Cancel))!.DirectoryName!;
        var old = Path.Combine(folder, ".download-old.zip");
        var recent = Path.Combine(folder, ".download-recent.zip");
        await File.WriteAllBytesAsync(old, [1], Cancel);
        await File.WriteAllBytesAsync(recent, [1], Cancel);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));

        using var response = await http.GetAsync(BackupEndpoints.LatestPath, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent), "A temporary file that may still be in use is kept.");
    }

    [Fact]
    public async Task Should_NotHandOutBackupsWithoutAccess()
    {
        await using var app = new TestWebApp(
            "Production",
            new Dictionary<string, string?>(AccessTokens.Settings));
        using var http = app.CreateClient();

        using var response = await http.GetAsync(BackupEndpoints.LatestPath, Cancel);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Should_ShowEverySection()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        await http.GetAsync("/", Cancel);

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));

        foreach (var text in new[] { "Relay board", "The board has no login", "People", John, "Backups", "No copy yet.", "Make a copy now" })
        {
            Assert.Contains(text, html, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("download data-enhance-nav", html, StringComparison.Ordinal);
    }

    private static TestWebApp CreateApp()
    {
        return new TestWebApp("Development", new Dictionary<string, string?> { ["CloudflareAccess:DevUserEmail"] = John });
    }
}
