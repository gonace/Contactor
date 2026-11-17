using Contactor.Tests.Boards;
using Contactor.Web.Components;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Languages;
using Contactor.Web.Persistence;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// "Join the step above" on the group's page, with a board whose relays are all off: what it shows, what it does to
/// the steps, and that nothing is saved until "Save".
/// </summary>
public sealed class GroupJoinStepTests
{
    private const string John = "john@example.com";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Heat (step 2 of 3) joins the fan: on the page at once, with Lamp as step 2 and its own wait and run-on, in the
    // database only after "Save".
    [Fact]
    public async Task Should_JoinTheStepAbove_AndSaveItOnlyOnSave()
    {
        await using var app = await StartAppAsync();
        var id = await AddGroupAsync(app, ("Fan", 1, 3, 300), ("Heat", 2, 10, 60), ("Lamp", 3, 5, 0));
        using var page = await PageRenderer.StartAsync<GroupDetail>(app.Services, John, "John", new Dictionary<string, object?> { ["Id"] = id });
        Assert.False(await page.HasAsync(PageRenderer.With("aria-label", "Move Fan into the step above")));
        Assert.True(await page.HasAsync(PageRenderer.With("aria-label", "Move Lamp into the step above")));

        await page.ClickAsync(PageRenderer.With("aria-label", "Move Heat into the step above"));

        Assert.Matches("STEP 1 · NAME[\\s\\S]*Fan[\\s\\S]*Heat[\\s\\S]*On start, wait[\\s\\S]*STEP 2 · NAME[\\s\\S]*Lamp", await page.TextAsync());
        Assert.False(await page.HasAsync(PageRenderer.With("id", "step-2")));
        Assert.Equal(["Fan: 1 :3:300", "Heat: 2 :10:60", "Lamp: 3 :5:0"], await SavedStepsAsync(app, id));

        await page.SubmitAsync(PageRenderer.With("id", "group-form"));

        // Saved: the page goes back to Relays, which the test's renderer reports as a navigation.
        Assert.IsType<NavigationException>(Assert.Single(page.Errors));
        Assert.Equal(["Fan: 1 2 :3:300", "Lamp: 3 :5:0"], await SavedStepsAsync(app, id));
    }

    // With two relays "Remove" is hidden, so "Join the step above" shows alone, and makes one step of the two.
    [Fact]
    public async Task Should_ShowJoinAlone_WithTwoRelays()
    {
        await using var app = await StartAppAsync();
        var id = await AddGroupAsync(app, ("Fan", 1, 3, 300), ("Heat", 2, 3, 0));
        using var page = await PageRenderer.StartAsync<GroupDetail>(app.Services, John, "John", new Dictionary<string, object?> { ["Id"] = id });
        Assert.False(await page.HasAsync(PageRenderer.WithText("Remove")));
        Assert.True(await page.HasAsync(PageRenderer.WithText("Join the step above")));

        await page.ClickAsync(PageRenderer.With("aria-label", "Move Heat into the step above"));
        await page.SubmitAsync(PageRenderer.With("id", "group-form"));

        // Saved: the page goes back to Relays, which the test's renderer reports as a navigation.
        Assert.IsType<NavigationException>(Assert.Single(page.Errors));
        Assert.Equal(["Fan: 1 2 :3:300"], await SavedStepsAsync(app, id));
    }

    // "Join the step above" shares CanChange with "Remove" and up/down: hidden while any of the group's relays is on,
    // and while the board has no contact.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_HideJoin_WhileTheGroupIsOnOrTheBoardHasNoContact(bool groupOn)
    {
        var board = groupOn ? new FakeRelayBoard() : FakeRelayBoard.LostContact();
        await using var app = await StartAppAsync(board);
        var id = await AddGroupAsync(app, ("Fan", 1, 3, 300), ("Heat", 2, 3, 0), ("Lamp", 3, 3, 0));
        if (groupOn)
        {
            board.SetRelay(1, true);
        }

        using var page = await PageRenderer.StartAsync<GroupDetail>(app.Services, John, "John", new Dictionary<string, object?> { ["Id"] = id });

        Assert.True(await page.HasAsync(PageRenderer.With("id", "step-2")));
        Assert.False(await page.HasAsync(PageRenderer.WithText("Join the step above")));
        Assert.False(await page.HasAsync(PageRenderer.WithText("Remove")));
    }

    [Fact]
    public async Task Should_SayJoinTheStepAbove_InSwedish()
    {
        await using var app = await StartAppAsync();
        await app.Services.GetRequiredService<AppLanguage>().ChooseAsync("sv", Cancel);
        var id = await AddGroupAsync(app, ("Fläkt", 1, 3, 300), ("Värme", 2, 3, 0));
        PageRenderer page;
        using (TestCulture.Language("sv"))
        {
            page = await PageRenderer.StartAsync<GroupDetail>(app.Services, John, "John", new Dictionary<string, object?> { ["Id"] = id });
        }

        using (page)
        {
            Assert.True(await page.HasAsync(PageRenderer.WithText("Gå med i steget ovanför")));
            Assert.True(await page.HasAsync(PageRenderer.With("aria-label", "Flytta Värme till steget ovanför")));
        }
    }

    private static async Task<TestWebApp> StartAppAsync(FakeRelayBoard? board = null)
    {
        var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John },
            TestWebApp.WithBoard(board ?? new FakeRelayBoard()));
        using var http = app.CreateClient();
        await http.GetAsync("/", Cancel);
        return app;
    }

    // A group on the first board, one relay a step, by channel: each step's name (its relay's too), channel, wait and
    // run-on.
    private static async Task<int> AddGroupAsync(TestWebApp app, params (string Name, int Channel, int StartDelaySec, int RunOnSec)[] steps)
    {
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel);
        var group = new RelayGroup { BoardId = 1, Name = "Fan heater", Icon = GroupIcons.Fan, MaxOnMinutes = 120 };
        foreach (var step in steps)
        {
            var relay = await db.Relays.SingleAsync(r => r.BoardId == 1 && r.Channel == step.Channel, Cancel);
            relay.Name = step.Name;
            await db.SaveChangesAsync(Cancel);
            group.AddStep(step.Name, relay.Id, startDelaySec: step.StartDelaySec, runOnSec: step.RunOnSec);
        }

        await db.AddWithRuntimesAsync(group);
        return group.Id;
    }

    // The group's saved steps in start order: each name with its relays' channels, its wait and run-on, e.g. "Fan: 1 2 :3:300".
    private static async Task<List<string>> SavedStepsAsync(TestWebApp app, int id)
    {
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel);
        var group = await db.RelayGroups.Include(g => g.Steps).ThenInclude(s => s.Members).ThenInclude(m => m.Relay).SingleAsync(g => g.Id == id, Cancel);
        return [.. group.Steps.OrderBy(s => s.Position).Select(s => $"{s.Name}: {string.Join("", s.Members.OrderBy(m => m.Order).Select(m => $"{m.Relay!.Channel} "))}:{s.StartDelaySec}:{s.RunOnSec}")];
    }
}
