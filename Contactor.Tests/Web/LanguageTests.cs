using System.Net;
using Contactor.Web.Components;
using Contactor.Web.Features.Cloudflare;
using Contactor.Web.Features.Languages;
using Contactor.Web.Persistence;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// The app's one language: saved with the installation, used by every request whatever the browser asks for, chosen on
/// the setup guide's first page and in Settings once there is a second language, and every open page reloading in it.
/// </summary>
public sealed class LanguageTests
{
    private const string John = "john@example.com";

    // A second language for the tests only: the app has English alone, so its choices are hidden.
    private static readonly Language TestSwedish = new("sv", "Svenska");

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_BeEnglish_UntilOneIsChosen()
    {
        await using var database = await DatabaseAsync();
        var language = database.Services.GetRequiredService<AppLanguage>();

        await language.ApplySavedAsync(Cancel);

        Assert.Equal("en", language.Current.Code);
        Assert.False(language.HasChoice);
    }

    [Fact]
    public async Task Should_SaveTheChoice_AndUseItAfterARestart()
    {
        await using var database = await DatabaseAsync(TwoLanguages);
        var language = database.Services.GetRequiredService<AppLanguage>();
        var changes = 0;
        language.Changed += (_, _) => changes++;

        Assert.True(await language.ChooseAsync("sv", Cancel));
        Assert.True(await language.ChooseAsync("sv", Cancel));

        Assert.Equal("sv", language.Current.Code);
        Assert.Equal(1, changes);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            // No installation row before: the choice makes it, as the guide's first page does.
            Assert.Equal("sv", (await db.Installations.SingleAsync(Cancel)).Language);
        }

        var restarted = new AppLanguage(database.Factory, new LanguageCatalogue([LanguageCatalogue.English, TestSwedish]), Microsoft.Extensions.Logging.Abstractions.NullLogger<AppLanguage>.Instance);
        await restarted.ApplySavedAsync(Cancel);
        Assert.Equal("sv", restarted.Current.Code);
    }

    [Fact]
    public async Task Should_RefuseALanguageItDoesNotHave_AndSaveNothing()
    {
        await using var database = await DatabaseAsync(TwoLanguages);
        var language = database.Services.GetRequiredService<AppLanguage>();
        var changes = 0;
        language.Changed += (_, _) => changes++;

        Assert.False(await language.ChooseAsync("xx", Cancel));
        Assert.False(await language.ChooseAsync("", Cancel));

        Assert.Equal("en", language.Current.Code);
        Assert.Equal(0, changes);
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.False(await db.Installations.AnyAsync(Cancel));
    }

    [Fact]
    public async Task Should_StartInEnglish_WhenTheSavedLanguageIsGone()
    {
        await using var database = await DatabaseAsync();
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            db.Installations.Add(new Installation { Language = "sv" });
            await db.SaveChangesAsync(Cancel);
        }

        var language = database.Services.GetRequiredService<AppLanguage>();
        await language.ApplySavedAsync(Cancel);

        Assert.Equal("en", language.Current.Code);
    }

    [Fact]
    public void Should_RefuseACatalogueWithoutLanguages_OrWithOneTwice()
    {
        Assert.Throws<ArgumentException>(() => new LanguageCatalogue([]));
        Assert.Throws<ArgumentException>(() => new LanguageCatalogue([LanguageCatalogue.English, new Language("EN", "English")]));
    }

    [Fact]
    public async Task Should_AnswerInTheAppsLanguage_NotTheBrowsers()
    {
        await using var app = new TestWebApp(configureServices: TwoLanguages);
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("sv-SE,sv;q=0.9");

        var english = await http.GetStringAsync("/settings", Cancel);
        Assert.Contains("<html lang=\"en\">", english, StringComparison.Ordinal);

        await app.Services.GetRequiredService<AppLanguage>().ChooseAsync("sv", Cancel);
        http.DefaultRequestHeaders.AcceptLanguage.Clear();
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");

        // The test language has no texts of its own, so its pages fall back to English's.
        var swedish = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));
        Assert.Contains("<html lang=\"sv\">", swedish, StringComparison.Ordinal);
        Assert.Contains("<title>Settings · Contactor</title>", swedish, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Main menu\"", swedish, StringComparison.Ordinal);
        Assert.Contains("<span class=\"reconnect-trying\">Reconnecting…</span>", swedish, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_HideTheChoice_WithOneLanguage()
    {
        await using var app = new TestWebApp();
        using var http = app.CreateClient();

        var settings = await http.GetStringAsync("/settings", Cancel);

        Assert.DoesNotContain("id=\"language\"", settings, StringComparison.Ordinal);
        using var page = await PageRenderer.StartAsync<Settings>(app.Services, John, "John");
        Assert.False(await page.HasAsync(PageRenderer.With("id", "language")));
    }

    [Fact]
    public async Task Should_ChooseTheLanguageInSettings_ForEveryone()
    {
        await using var app = new TestWebApp(configureServices: TwoLanguages);
        using var http = app.CreateClient();
        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));
        Assert.Contains("One language for everyone who uses the app.", html, StringComparison.Ordinal);
        Assert.Matches("<option value=\"en\" lang=\"en\" selected[^>]*>English</option>", html);
        Assert.Matches("<option value=\"sv\" lang=\"sv\"( b-[a-z0-9]+)?>Svenska</option>", html);

        using var page = await PageRenderer.StartAsync<Settings>(app.Services, John, "John");
        await page.TypeAsync(PageRenderer.With("id", "language"), "sv");

        Assert.Equal("sv", app.Services.GetRequiredService<AppLanguage>().Current.Code);
        Assert.Empty(page.Errors);
    }

    [Fact]
    public async Task Should_OfferTheLanguage_OnTheGuidesFirstPage()
    {
        await using var app = new TestWebApp(configureServices: TwoLanguages, setUp: false);
        using var http = app.CreateClient();
        await http.GetAsync("/setup", Cancel);

        using var page = await PageRenderer.StartAsync<SetupGuide>(app.Services, John, "John");
        Assert.Contains("How do people reach the app?", await page.TextAsync(), StringComparison.Ordinal);
        await page.TypeAsync(PageRenderer.With("id", "language"), "sv");

        Assert.Equal("sv", app.Services.GetRequiredService<AppLanguage>().Current.Code);
        Assert.Empty(page.Errors);
    }

    [Fact]
    public async Task Should_NotOfferTheLanguage_OnTheGuidesLaterPages()
    {
        await using var app = new TestWebApp(
            settings: new Dictionary<string, string?> { ["CloudflareAccess:TeamDomain"] = "myteam.cloudflareaccess.com", ["CloudflareAccess:Audience"] = "aud" },
            configureServices: TwoLanguages,
            setUp: false);
        app.SaveBeforeStart(new Installation { AccessType = CloudflareMethods.TunnelId, SignInType = CloudflareMethods.AccessId });
        using var http = app.CreateClient();
        await http.GetAsync("/setup", Cancel);

        using var page = await PageRenderer.StartAsync<SetupGuide>(app.Services, John, "John");

        Assert.Contains("About you", await page.TextAsync(), StringComparison.Ordinal);
        Assert.False(await page.HasAsync(PageRenderer.With("id", "language")));
    }

    [Fact]
    public async Task Should_ReloadEveryOpenPage_WhenTheLanguageChanges()
    {
        await using var app = new TestWebApp(configureServices: TwoLanguages);
        using var http = app.CreateClient();
        await http.GetAsync("/", Cancel);
        using var first = await PageRenderer.StartAsync<LanguageWatch>(app.Services, John, "John");
        using var second = await PageRenderer.StartAsync<LanguageWatch>(app.Services, John, "John");
        Assert.Empty(first.Errors);

        await app.Services.GetRequiredService<AppLanguage>().ChooseAsync("sv", Cancel);

        // Without a browser, going anywhere throws NavigationException: here, to the same page, reloaded.
        foreach (var page in new[] { first, second })
        {
            var reload = Assert.IsType<NavigationException>(await ErrorAsync(page));
            Assert.Equal("http://localhost/", reload.Location);
        }
    }

    [Fact]
    public async Task Should_StopWatching_OnceThePageCloses()
    {
        await using var app = new TestWebApp(configureServices: TwoLanguages);
        using var http = app.CreateClient();
        await http.GetAsync("/", Cancel);
        var page = await PageRenderer.StartAsync<LanguageWatch>(app.Services, John, "John");
        page.Close();

        await app.Services.GetRequiredService<AppLanguage>().ChooseAsync("sv", Cancel);

        Assert.Empty(page.Errors);
    }

    private static void TwoLanguages(IServiceCollection services)
    {
        services.AddSingleton(new LanguageCatalogue([LanguageCatalogue.English, TestSwedish]));
    }

    private static async Task<TestDatabase> DatabaseAsync(Action<IServiceCollection>? configure = null)
    {
        var database = TestDatabase.Create((services, _) =>
        {
            configure?.Invoke(services);
            services.AddLanguages(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        });
        await database.Services.InitializeDatabaseAsync(Cancel);
        return database;
    }

    // The reload reaches the page on its own connection, a moment after the choice.
    private static async Task<Exception> ErrorAsync(PageRenderer page)
    {
        await Wait.UntilAsync(() => page.Errors.Count > 0);
        return Assert.Single(page.Errors);
    }
}
