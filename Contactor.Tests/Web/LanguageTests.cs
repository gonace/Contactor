using System.Globalization;
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
    public async Task Should_AskForTheLanguage_AsTheGuidesFirstStep()
    {
        await using var app = new TestWebApp(configureServices: TwoLanguages, setUp: false);
        using var http = app.CreateClient();
        await http.GetAsync("/setup", Cancel);
        Assert.Contains("STEP 1 OF 5", await http.GetStringAsync("/setup/code", Cancel), StringComparison.Ordinal);

        using var page = await PageRenderer.StartAsync<SetupGuide>(app.Services, John, "John");
        var text = await page.TextAsync();
        Assert.Contains("STEP 2 OF 5", text, StringComparison.Ordinal);
        Assert.Contains("The language of Contactor, for everyone who uses it.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("How do people reach the app?", text, StringComparison.Ordinal);
        Assert.False(await page.HasAsync(PageRenderer.WithText("Back")));
        Assert.True(await page.HasAsync(attributes => attributes.GetValueOrDefault("id") as string == "language"
            && attributes.GetValueOrDefault("aria-labelledby") as string == "language-heading"));
        await page.TypeAsync(PageRenderer.With("id", "language"), "sv");
        Assert.Equal("sv", app.Services.GetRequiredService<AppLanguage>().Current.Code);

        await page.ClickAsync(Button("Next"));
        text = await page.TextAsync();
        Assert.Contains("How do people reach the app?", text, StringComparison.Ordinal);
        Assert.Contains("STEP 3 OF 5", text, StringComparison.Ordinal);
        Assert.False(await page.HasAsync(PageRenderer.With("id", "language")));

        await page.ClickAsync(Button("Back"));
        Assert.Contains("STEP 2 OF 5", await page.TextAsync(), StringComparison.Ordinal);
        Assert.True(await page.HasAsync(PageRenderer.With("id", "language")));

        // A new step's heading takes the focus, which needs a browser.
        Assert.DoesNotContain(page.Errors, e => !e.ToString().Contains("ElementReference has not been configured correctly", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_StartTheGuideAtTheWayIn_WithOneLanguage()
    {
        await using var app = new TestWebApp(setUp: false);
        using var http = app.CreateClient();
        await http.GetAsync("/setup", Cancel);
        Assert.Contains("STEP 1 OF 4", await http.GetStringAsync("/setup/code", Cancel), StringComparison.Ordinal);

        using var page = await PageRenderer.StartAsync<SetupGuide>(app.Services, John, "John");

        var text = await page.TextAsync();
        Assert.Contains("How do people reach the app?", text, StringComparison.Ordinal);
        Assert.Contains("STEP 2 OF 4", text, StringComparison.Ordinal);
        Assert.False(await page.HasAsync(PageRenderer.With("id", "language")));
        Assert.False(await page.HasAsync(PageRenderer.WithText("Back")));
    }

    [Fact]
    public async Task Should_KeepTheLanguage_AndSaySo_WhenItCannotBeSaved()
    {
        await using var app = new TestWebApp(configureServices: TwoLanguages);
        using var http = app.CreateClient();
        await http.GetAsync("/settings", Cancel);
        using var page = await PageRenderer.StartAsync<Settings>(app.Services, John, "John");
        await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel))
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Installation RENAME TO InstallationGone", Cancel);
        }

        await page.TypeAsync(PageRenderer.With("id", "language"), "sv");

        Assert.Equal("en", app.Services.GetRequiredService<AppLanguage>().Current.Code);
        Assert.Contains("The language couldn't be saved. Try again.", await page.TextAsync(), StringComparison.Ordinal);
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
    public async Task Should_Reload_WhenTheLanguageChangedBeforeThePageConnected()
    {
        await using var app = new TestWebApp(configureServices: TwoLanguages);
        using var http = app.CreateClient();
        await http.GetAsync("/", Cancel);
        await app.Services.GetRequiredService<AppLanguage>().ChooseAsync("sv", Cancel);

        // The connection started in English, before the change.
        using var page = await InCultureAsync("en", () => PageRenderer.StartAsync<LanguageWatch>(app.Services, John, "John"));

        var reload = Assert.IsType<NavigationException>(await ErrorAsync(page));
        Assert.Equal("http://localhost/", reload.Location);
    }

    [Theory]
    [InlineData("sv")]
    [InlineData("fr")]
    [InlineData("")]
    public async Task Should_NotReload_InTheAppsLanguage_OrOneTheAppDoesNotHave(string culture)
    {
        await using var app = new TestWebApp(configureServices: TwoLanguages);
        using var http = app.CreateClient();
        await http.GetAsync("/", Cancel);
        await app.Services.GetRequiredService<AppLanguage>().ChooseAsync("sv", Cancel);

        // "fr" and the invariant culture are never a request's: one would reload for ever.
        using var page = await InCultureAsync(culture, () => PageRenderer.StartAsync<LanguageWatch>(app.Services, John, "John"));

        Assert.True(page.Renders > 0);
        Assert.Empty(page.Errors);
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

    // Runs start with this culture, as a page's connection has its request's; the test's own culture is put back.
    private static async Task<T> InCultureAsync<T>(string culture, Func<Task<T>> start)
    {
        var (before, beforeUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            return await start();
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (before, beforeUi);
        }
    }

    // A button by its words: only buttons have a type.
    private static Func<IReadOnlyDictionary<string, object?>, bool> Button(string text)
    {
        return attributes => attributes.ContainsKey("type") && PageRenderer.WithText(text)(attributes);
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
        var error = Assert.Single(page.Errors);

        // From the page's first render, it comes wrapped.
        return error is AggregateException { InnerExceptions: [var inner] } ? inner : error;
    }
}
