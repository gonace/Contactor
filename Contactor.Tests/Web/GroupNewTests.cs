using System.Net;
using Contactor.Tests.Boards;
using Contactor.Web.Components;
using Contactor.Web.Features.Groups;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// "New group": what it offers, its steps as the group's page edits them, and the plain-language plan it shows before
/// creating.
/// </summary>
public sealed class GroupNewTests
{
    private const string John = "john@example.com";
    private static readonly GroupMemberDraft[] FanHeater =
    [
        new(3, "Fan", 3, 300),
        new(1, "Heat", 10, 0),
        new(5, "Lamp", 1, 0),
    ];

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_DescribeTheStartFromTheTop_WithTheWaits()
    {
        Assert.Equal("Fan → wait 10 s → Heat → wait 1 s → Lamp", TestTexts.GroupPlan.Start(FanHeater));
    }

    [Fact]
    public void Should_DescribeTheStopFromTheBottom_WithTheRunOn()
    {
        Assert.Equal("Lamp off → Heat off → Run-on for 5 min: Fan, then off", TestTexts.GroupPlan.Stop(FanHeater));
    }

    [Fact]
    public void Should_DescribeTheInterlockForEachPair()
    {
        Assert.Equal(
            "Heat can only be on while Fan is on. Lamp can only be on while Heat is on.",
            TestTexts.GroupPlan.Interlock(FanHeater));
    }

    [Theory]
    [InlineData(null, false, true, "Free", true)]
    [InlineData(null, false, false, "Inactive · free", true)]
    [InlineData("Painting", false, true, "Already in Painting", false)]
    [InlineData(null, true, true, "Has its own schedules", false)]
    public void Should_SayWhetherARelayCanJoin(string? group, bool hasSchedules, bool isActive, string note, bool isFree)
    {
        var relay = new GroupCandidate(4, 4, "Relay 4", isActive, group, hasSchedules);

        Assert.Equal((note, isFree), (TestTexts.GroupPlan.Note(relay), relay.IsFree));
    }

    [Fact]
    public async Task Should_StartWithNameAndIcon()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = "john@example.com" });
        using var http = app.CreateClient();

        var list = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));
        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/group/new", Cancel));

        Assert.Contains("href=\"group/new\"", list, StringComparison.Ordinal);
        Assert.Contains("STEP 1 OF 4", html, StringComparison.Ordinal);
        Assert.Contains("Name and icon", html, StringComparison.Ordinal);
        Assert.Matches("type=\"radio\"[^>]*value=\"fan\" checked", html);
        Assert.Contains("Tools", html, StringComparison.Ordinal);
    }

    // Four relays picked, each a step of its own; relay 2 then joins the step above, and relay 4 is taken out and added
    // to the step above it. The group is saved with two steps of two relays, as the group's page saves them.
    [Fact]
    public async Task Should_MakeAGroupOfTwoTwoRelaySteps()
    {
        await using var app = await StartAppAsync();
        var relays = await RelaysAsync(app);
        using var page = await AtOrderAndRulesAsync(app, relays.Take(4));
        Assert.Equal(3, await CountAsync(page, "On start, wait"));

        await JoinStepAboveAsync(page, relays[1], channel: 2, step: 0);
        await JoinStepAboveAsync(page, relays[3], channel: 4, step: 1);
        Assert.Equal(1, await CountAsync(page, "On start, wait"));
        await page.TypeAsync(PageRenderer.With("id", "step-0"), "Fans");
        await page.TypeAsync(PageRenderer.With("id", "step-1"), "Heaters");
        Assert.Matches("STEP 1 · NAME[\\s\\S]*Relay 1[\\s\\S]*Relay 2[\\s\\S]*On start, wait[\\s\\S]*STEP 2 · NAME[\\s\\S]*Relay 3[\\s\\S]*Relay 4", await page.TextAsync());
        Assert.False(await page.HasAsync(PageRenderer.With("id", "step-2")));

        await page.ClickAsync(Button("Next"));
        var review = await page.TextAsync();
        Assert.Contains("GROUP · RELAY 1 + 2 → 3 + 4", review, StringComparison.Ordinal);
        Assert.Contains("Fans → wait 3 s → Heaters", review, StringComparison.Ordinal);
        Assert.Contains("Heaters can only be on while Fans is on.", review, StringComparison.Ordinal);

        await page.ClickAsync(Button("Create group"));
        Assert.Equal([$"Fans: {relays[0]} {relays[1]}", $"Heaters: {relays[2]} {relays[3]}"], await SavedStepsAsync(app));
    }

    // "Remove" takes a relay out, and its step with it; up/down moves a whole step. With two relays left, "Remove" is gone.
    [Fact]
    public async Task Should_RemoveARelay_AndMoveAStep()
    {
        await using var app = await StartAppAsync();
        var relays = await RelaysAsync(app);
        using var page = await AtOrderAndRulesAsync(app, relays.Take(3));

        await page.ClickAsync(PageRenderer.With("aria-label", "Remove Relay 3 from the group"));
        Assert.False(await page.HasAsync(PageRenderer.With("id", "step-2")));
        Assert.False(await page.HasAsync(PageRenderer.With("class", "remove")));
        await page.ClickAsync(PageRenderer.With("aria-label", "Move Relay 2 up"));
        Assert.Matches("STEP 1 · NAME[\\s\\S]*Relay 2[\\s\\S]*STEP 2 · NAME[\\s\\S]*Relay 1", await page.TextAsync());

        await page.ClickAsync(Button("Next"));
        Assert.Contains("GROUP · RELAY 2 → 1", await page.TextAsync(), StringComparison.Ordinal);
        await page.ClickAsync(Button("Create group"));
        Assert.Equal([$"Relay 2: {relays[1]}", $"Relay 1: {relays[0]}"], await SavedStepsAsync(app));
    }

    // "Next" waits for every step to have a name.
    [Fact]
    public async Task Should_GoOn_OnlyWithEveryStepNamed()
    {
        await using var app = await StartAppAsync();
        var relays = await RelaysAsync(app);
        using var page = await AtOrderAndRulesAsync(app, relays.Take(2));

        await page.TypeAsync(PageRenderer.With("id", "step-1"), " ");
        Assert.True(await NextDisabledAsync(page));
        await page.TypeAsync(PageRenderer.With("id", "step-1"), "Heat");
        Assert.False(await NextDisabledAsync(page));
    }

    private static async Task<TestWebApp> StartAppAsync()
    {
        var app = new TestWebApp(configureServices: TestWebApp.WithBoard(new FakeRelayBoard()));
        using var http = app.CreateClient();
        await http.GetAsync("/", Cancel);
        return app;
    }

    // The board's relays' ids, by channel.
    private static async Task<List<int>> RelaysAsync(TestWebApp app)
    {
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel);
        return await db.Relays.OrderBy(r => r.Channel).Select(r => r.Id).ToListAsync(Cancel);
    }

    // "New group" on "Order and rules", with these relays picked in this order.
    private static async Task<PageRenderer> AtOrderAndRulesAsync(TestWebApp app, IEnumerable<int> relayIds)
    {
        var page = await PageRenderer.StartAsync<GroupNew>(app.Services, John, "John");
        await page.TypeAsync(PageRenderer.With("id", "name"), "Workshop heat");
        await page.ClickAsync(Button("Next"));
        foreach (var id in relayIds)
        {
            await page.ClickAsync(PageRenderer.With("id", $"pick-{id}"));
        }

        await page.ClickAsync(Button("Next"));
        Assert.Contains("Order and rules", await page.TextAsync(), StringComparison.Ordinal);
        return page;
    }

    // A relay in a step of its own joins the step above: "Remove", then "+ Add a relay to this step" on that step.
    private static async Task JoinStepAboveAsync(PageRenderer page, int relayId, int channel, int step)
    {
        await page.ClickAsync(PageRenderer.With("aria-label", $"Remove Relay {channel} from the group"));
        await page.ClickAsync(Button("Add a relay to this step"), step);
        await page.TypeAsync(PageRenderer.With("id", $"add-relay-{step}"), relayId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await page.ClickAsync(Button("Add"));
    }

    private static Task<bool> NextDisabledAsync(PageRenderer page)
    {
        return page.HasAsync(attributes => Button("Next")(attributes) && attributes.GetValueOrDefault("disabled") is true);
    }

    private static async Task<int> CountAsync(PageRenderer page, string text)
    {
        return (await page.TextAsync()).Split(text).Length - 1;
    }

    // The one saved group's steps in start order: each name with its relays' ids in order, e.g. "Fans: 1 2".
    private static async Task<List<string>> SavedStepsAsync(TestWebApp app)
    {
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel);
        var group = await db.RelayGroups.Include(g => g.Steps).ThenInclude(s => s.Members).SingleAsync(Cancel);
        return [.. group.Steps.OrderBy(s => s.Position).Select(s => $"{s.Name}: {string.Join(' ', s.Members.OrderBy(m => m.Order).Select(m => m.RelayId))}")];
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool> Button(string text)
    {
        return attributes => attributes.ContainsKey("type") && PageRenderer.WithText(text)(attributes);
    }
}
