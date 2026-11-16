using System.Net;
using System.Text.RegularExpressions;
using Contactor.Web.Components;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Inputs;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// The Relays screen's line for each input that controls a relay or a group, under its status: "Door: on when it
/// opens".
/// </summary>
public sealed partial class RelayInputLinesTests
{
    private const string John = "john@example.com";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_SayWhatAnInputDoes_InTheInputScreensWords()
    {
        Assert.Equal("on when it opens", TestTexts.Inputs.Does(null, true));
        Assert.Equal("off when it closes", TestTexts.Inputs.Does(false, null));
        Assert.Equal("on when it closes, off when it opens", TestTexts.Inputs.Does(true, false));
        Assert.Equal("off when it closes, on when it opens", TestTexts.Inputs.Does(false, true));
        Assert.Equal("", TestTexts.Inputs.Does(null, null));
    }

    [Fact]
    public async Task Should_ShowTheInput_OnTheCardOfTheRelayItControls()
    {
        await using var app = new TestWebApp("Development", new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John }, TestWebApp.WithLostBoard);
        await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel))
        {
            (await db.Inputs.SingleAsync(i => i.Id == 1, Cancel)).Name = "Door";
            db.InputActions.Add(new InputAction { InputId = 1, RelayId = 3, WhenOpened = true });
            await db.SaveChangesAsync(Cancel);
        }

        using var http = app.CreateClient();
        var html = await http.GetStringAsync("/", Cancel);

        Assert.Contains("Door: on when it opens", CardText(html, "relay/3"), StringComparison.Ordinal);
        Assert.Contains("class=\"input-badge\"", Card(html, "relay/3"), StringComparison.Ordinal);
        Assert.DoesNotContain("Door:", CardText(html, "relay/4"), StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(html, "class=\"input-line\""));
    }

    [Fact]
    public async Task Should_ShowALinePerInput_WhenTwoInputsControlARelay()
    {
        await using var boards = await TwoBoards.StartAsync();
        int relayId;
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            relayId = await db.Relays.Where(r => r.BoardId == boards.BarnId && r.Channel == 1).Select(r => r.Id).SingleAsync(Cancel);
            var inputs = await db.Inputs.Where(i => i.BoardId == boards.BarnId).OrderBy(i => i.Channel).ToListAsync(Cancel);
            (inputs[0].Name, inputs[1].Name) = ("Door", "Pressure switch");
            db.InputActions.Add(new InputAction { InputId = inputs[1].Id, RelayId = relayId, WhenClosed = true, WhenOpened = false });
            db.InputActions.Add(new InputAction { InputId = inputs[0].Id, RelayId = relayId, WhenOpened = true });
            await db.SaveChangesAsync(Cancel);
        }

        using var http = boards.App.CreateClient();
        var card = CardText(await http.GetStringAsync("/", Cancel), $"relay/{relayId}");

        // In the order of the board's inputs.
        var door = card.IndexOf("Door: on when it opens", StringComparison.Ordinal);
        var pressure = card.IndexOf("Pressure switch: on when it closes, off when it opens", StringComparison.Ordinal);
        Assert.True(door >= 0 && pressure > door, card);
    }

    [Fact]
    public async Task Should_ShowTheInput_OnTheCardOfTheGroupItControls()
    {
        await using var app = new TestWebApp("Development", new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John }, TestWebApp.WithLostBoard);
        int groupId;
        await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel))
        {
            var group = new RelayGroup { BoardId = 1, Name = "Fan heater", Icon = GroupIcons.Fan, MaxOnMinutes = 120 };
            group.AddStep("Fan", 1);
            group.AddStep("Heat", 2);
            await db.AddWithRuntimesAsync(group);
            groupId = group.Id;
            (await db.Inputs.SingleAsync(i => i.Id == 1, Cancel)).Name = "Door";
            db.InputActions.Add(new InputAction { InputId = 1, GroupId = groupId, WhenOpened = false });
            await db.SaveChangesAsync(Cancel);
        }

        using var http = app.CreateClient();
        var html = await http.GetStringAsync("/", Cancel);

        Assert.Contains("Door: off when it opens", CardText(html, $"group/{groupId}"), StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(html, "class=\"input-line\""));
    }

    // Only what the input would act on: nothing for an action that does nothing, or for a relay of another board.
    [Fact]
    public async Task Should_ShowNoLine_ForAnActionThatDoesNothing_OrAnotherBoardsInput()
    {
        await using var boards = await TwoBoards.StartAsync();
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            var barnInput = await db.Inputs.Where(i => i.BoardId == boards.BarnId).Select(i => i.Id).FirstAsync(Cancel);
            db.InputActions.Add(new InputAction { InputId = 1, RelayId = 3 });
            db.InputActions.Add(new InputAction { InputId = barnInput, RelayId = 4, WhenClosed = true });
            await db.SaveChangesAsync(Cancel);
        }

        using var http = boards.App.CreateClient();
        var html = await http.GetStringAsync("/", Cancel);

        Assert.DoesNotContain("class=\"input-line\"", html, StringComparison.Ordinal);
    }

    // The Input screen saves: the line comes, and goes again when the action is removed.
    [Fact]
    public async Task Should_AddAndRemoveTheLine_WhenTheInputScreenSaves()
    {
        await using var app = new TestWebApp("Development", new Dictionary<string, string?> { [TestWebApp.SignedInAs] = John }, TestWebApp.WithLostBoard);
        using var page = await PageRenderer.StartAsync<Relays>(app.Services, John, "John");
        var actions = app.Services.GetRequiredService<InputActionsHandler>();
        Assert.DoesNotContain("Input 1: ", await page.TextAsync(), StringComparison.Ordinal);

        Assert.Null(await actions.SaveAsync(1, [new InputActionRow(new InputActionTarget(5, 5, null, "Relay 5"), true, false)], Cancel));
        await Wait.UntilAsync(() => page.TextAsync().GetAwaiter().GetResult().Contains("Input 1: on when it closes, off when it opens", StringComparison.Ordinal));

        Assert.Null(await actions.SaveAsync(1, [], Cancel));
        await Wait.UntilAsync(() => !page.TextAsync().GetAwaiter().GetResult().Contains("Input 1: ", StringComparison.Ordinal));
        Assert.Empty(page.Errors);
    }

    // A board removed while the page is open takes its inputs and their lines with it.
    [Fact]
    public async Task Should_DropTheLines_OfARemovedBoardsInputs()
    {
        await using var boards = await TwoBoards.StartAsync();
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            var relayId = await db.Relays.Where(r => r.BoardId == boards.BarnId && r.Channel == 1).Select(r => r.Id).SingleAsync(Cancel);
            var input = await db.Inputs.Where(i => i.BoardId == boards.BarnId).OrderBy(i => i.Channel).FirstAsync(Cancel);
            input.Name = "Barn door";
            db.InputActions.Add(new InputAction { InputId = input.Id, RelayId = relayId, WhenClosed = true });
            await db.SaveChangesAsync(Cancel);
        }

        using var page = await PageRenderer.StartAsync<Relays>(boards.App.Services, John, "John");
        Assert.Contains("Barn door: on when it closes", await page.TextAsync(), StringComparison.Ordinal);

        Assert.Null(await boards.App.Services.GetRequiredService<BoardRemoval>().RemoveAsync(boards.BarnId, turnOffFirst: false, John, Cancel));

        await Wait.UntilAsync(() => !page.TextAsync().GetAwaiter().GetResult().Contains("Barn door", StringComparison.Ordinal));
        Assert.Empty(page.Errors);
    }

    // One card's markup: from its link to the next card.
    private static string Card(string html, string href)
    {
        var start = html.IndexOf($"href=\"{href}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No card for {href}.");
        var end = html.IndexOf("class=\"card", start, StringComparison.Ordinal);
        return html[start..(end < 0 ? html.Length : end)];
    }

    // One card's text, without its markup.
    private static string CardText(string html, string href)
    {
        return WebUtility.HtmlDecode(Tags().Replace(Card(html, href), ""));
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
