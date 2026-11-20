using System.Globalization;
using Contactor.Web.Components;
using Contactor.Web.Features.Languages;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// The app in Swedish: a page redrawn by a change from another thread stays Swedish, and the language is put back
/// after use.
/// </summary>
public sealed class SwedishTests
{
    private const string John = "john@example.com";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // A board's change comes on a thread of its own, in English: the page it redraws stays Swedish.
    [Fact]
    public async Task Should_StayInSwedish_WhenAChangeFromAnotherThreadRedrawsThePage()
    {
        await using var app = await SwedishAppAsync();
        PageRenderer page;
        using (TestCulture.Language("sv"))
        {
            page = await PageRenderer.StartAsync<Relays>(app.Services, John, "John");
        }

        using (page)
        {
            var renders = page.Renders;
            Assert.Equal("en", CultureInfo.CurrentUICulture.Name);

            app.Services.GetRequiredService<RelayChanges>().Notify();
            await Wait.UntilAsync(() => page.Renders > renders);

            var text = await page.TextAsync();
            Assert.Contains("Hej John", text, StringComparison.Ordinal);
            Assert.Contains("Allt av", text, StringComparison.Ordinal);
            Assert.DoesNotContain("All off", text, StringComparison.Ordinal);
            Assert.Empty(page.Errors);
        }
    }

    // As for the Relays page: a group's page and the tab bar redrawn by a change from another thread stay Swedish.
    [Fact]
    public async Task Should_KeepAGroupsPageInSwedish_WhenAChangeFromAnotherThreadRedrawsIt()
    {
        await using var app = await SwedishAppAsync();
        var factory = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        int id;
        await using (var db = await factory.CreateDbContextAsync(Cancel))
        {
            var group = new RelayGroup { BoardId = 1, Name = "Fläktvärmare", Icon = GroupIcons.Fan, MaxOnMinutes = 120 };
            group.AddStep("Fläkt", 1, runOnSec: 300);
            group.AddStep("Värme", 2);
            await db.AddWithRuntimesAsync(group);
            id = group.Id;
        }

        PageRenderer page;
        using (TestCulture.Language("sv"))
        {
            page = await PageRenderer.StartAsync<GroupDetail>(app.Services, John, "John", new Dictionary<string, object?> { ["Id"] = id });
        }

        using (page)
        {
            await AssertSwedishAfterRedrawAsync(app, page, "SLÅ PÅ GRUPPEN", "TURN ON THE GROUP");
        }
    }

    [Fact]
    public async Task Should_KeepTheTabBarInSwedish_WhenAChangeFromAnotherThreadRedrawsIt()
    {
        await using var app = await SwedishAppAsync();
        PageRenderer page;
        using (TestCulture.Language("sv"))
        {
            page = await PageRenderer.StartAsync<TabBar>(app.Services, John, "John");
        }

        using (page)
        {
            await AssertSwedishAfterRedrawAsync(app, page, "Inställningar", "Settings");
        }
    }

    [Fact]
    public async Task Should_PutTheLanguageBack_AfterUsingIt()
    {
        await using var app = await SwedishAppAsync();
        var language = app.Services.GetRequiredService<AppLanguage>();

        using (language.Use())
        {
            Assert.Equal(("sv", "sv"), (CultureInfo.CurrentCulture.Name, CultureInfo.CurrentUICulture.Name));
        }

        Assert.Equal(("en", "en"), (CultureInfo.CurrentCulture.Name, CultureInfo.CurrentUICulture.Name));
    }

    // A change from a thread of the test's own, in English (the relays' "In use", a group made): the page redraws, and
    // its texts stay Swedish.
    private static async Task AssertSwedishAfterRedrawAsync(TestWebApp app, PageRenderer page, string swedish, string english)
    {
        var renders = page.Renders;
        Assert.Equal("en", CultureInfo.CurrentUICulture.Name);

        app.Services.GetRequiredService<RelayChanges>().Notify();
        await Wait.UntilAsync(() => page.Renders > renders);

        var text = await page.TextAsync();
        Assert.Contains(swedish, text, StringComparison.Ordinal);
        Assert.DoesNotContain(english, text, StringComparison.Ordinal);
        Assert.Empty(page.Errors);
    }

    // The app with Swedish chosen; every request has it, whatever the browser asks for.
    private static async Task<TestWebApp> SwedishAppAsync()
    {
        var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John },
            TestWebApp.WithBoard(Contactor.Tests.Boards.FakeRelayBoard.LostContact()));
        using (var http = app.CreateClient())
        {
            await http.GetAsync("/", Cancel);
        }

        await app.Services.GetRequiredService<AppLanguage>().ChooseAsync("sv", Cancel);
        return app;
    }
}
