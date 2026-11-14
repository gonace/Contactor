using System.Net;
using Contactor.Boards;
using Contactor.Tests.Boards;
using Contactor.Web.Components;
using Contactor.Web.Features.Boards;
using Contactor.Web.Persistence;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// Settings with two boards, a list of them, and a board's own page (<c>/board/{id}</c>): its name, address and login,
/// and "Remove", which asks first. With one board Settings is as it always was (SettingsPageTests).
/// </summary>
public sealed class BoardPagesTests
{
    private const string John = TwoBoards.John;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ListTheBoards_OnSettings_WithTwoOrMore()
    {
        await using var boards = await TwoBoards.StartAsync(FakeRelayBoard.LostContact(relayCount: 4, inputCount: 2));
        using var http = boards.App.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/settings", Cancel));

        Assert.Matches("<h2[^>]*>Relay boards</h2>", html);
        Assert.Contains("Tap a board to change its name, address or login, or to remove it.", html, StringComparison.Ordinal);
        Assert.Matches("<a href=\"board/1\" class=\"board-row\"", html);
        Assert.Matches($"<a href=\"board/{boards.BarnId}\" class=\"board-row lost\"", html);
        Assert.Contains("Test Fake · 4 relays · 192.168.1.50", html, StringComparison.Ordinal);
        Assert.Contains("Online", html, StringComparison.Ordinal);
        Assert.Matches("No contact since \\d\\d:\\d\\d|No contact since", html);
        Assert.DoesNotContain("id=\"host\"", html, StringComparison.Ordinal);
        Assert.True(html.IndexOf($"href=\"board/{boards.BarnId}\"", StringComparison.Ordinal) > html.IndexOf("href=\"board/1\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_ShowTheBoardsPage_WithItsNameAndAddress_AndNeverItsPassword()
    {
        await using var boards = await TwoBoards.StartAsync();
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            await db.Boards.Where(b => b.Id == boards.BarnId).ExecuteUpdateAsync(
                b => b.SetProperty(x => x.Host, "192.168.1.70").SetProperty(x => x.Port, 502).SetProperty(x => x.Username, "admin").SetProperty(x => x.Password, "quince7"),
                Cancel);
        }

        using var http = boards.App.CreateClient();
        var html = WebUtility.HtmlDecode(await http.GetStringAsync($"/board/{boards.BarnId}", Cancel));

        Assert.Matches("<h1[^>]*>Barn</h1>", html);
        Assert.Matches("<a href=\"settings\" class=\"back\"", html);
        Assert.Matches("id=\"board-name\"[^>]*value=\"Barn\"", html);
        Assert.Matches("id=\"host\"[^>]*value=\"192.168.1.70\"", html);
        Assert.Matches("id=\"port\"[^>]*value=\"502\"", html);
        Assert.Contains("Test Fake · 4 relays", html, StringComparison.Ordinal);
        Assert.Matches("<h2[^>]*>Remove board</h2>", html);
        Assert.Contains("Remove Barn", html, StringComparison.Ordinal);
        Assert.DoesNotContain("quince7", html, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"alertdialog\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\"", html, StringComparison.Ordinal);

        var missing = WebUtility.HtmlDecode(await http.GetStringAsync("/board/999", Cancel));
        Assert.Contains("This relay board isn't in Contactor.", missing, StringComparison.Ordinal);
    }

    // Saving on a board's page changes that board only: its name (on its row and on every screen), address and login.
    [Fact]
    public async Task Should_SaveTheNameAddressAndLogin_OfThatBoardOnly()
    {
        await using var boards = await TwoBoards.StartAsync();
        var handler = boards.App.Services.GetRequiredService<BoardAddressHandler>();
        Board first;
        await using (var db = await boards.Factory.CreateDbContextAsync(Cancel))
        {
            first = await db.Boards.AsNoTracking().SingleAsync(b => b.Id == 1, Cancel);
        }

        Assert.Equal("Enter a name.", await handler.RenameAsync(boards.BarnId, "  ", Cancel));
        Assert.Equal("The name can be at most 40 characters.", await handler.RenameAsync(boards.BarnId, new string('x', 41), Cancel));
        Assert.Null(await handler.RenameAsync(boards.BarnId, "  Garage ", Cancel));
        Assert.Null(await handler.SaveAsync(boards.BarnId, "192.168.1.71", 503, John, Cancel));
        Assert.Null(await handler.SaveLoginAsync(boards.BarnId, "", "", none: true, John, Cancel));

        await using var check = await boards.Factory.CreateDbContextAsync(Cancel);
        var barn = await check.Boards.SingleAsync(b => b.Id == boards.BarnId, Cancel);
        Assert.Equal(("Garage", "192.168.1.71", 503), (barn.Name, barn.Host, barn.Port));
        Assert.Equal("Garage", boards.App.Services.GetRequiredService<BoardRegistry>().Find(boards.BarnId)!.Name);
        var firstNow = await check.Boards.AsNoTracking().SingleAsync(b => b.Id == 1, Cancel);
        Assert.Equal((first.Name, first.Host, first.Port, first.Username, first.Password), (firstNow.Name, firstNow.Host, firstNow.Port, firstNow.Username, firstNow.Password));
    }

    // "Remove Barn" only asks, listing what goes; Cancel changes nothing; "Remove" turns its relays off, removes it and
    // goes to Settings.
    [Fact]
    public async Task Should_AskFirst_WithWhatIsDeleted_ThenRemoveAndGoToSettings()
    {
        await using var boards = await TwoBoards.StartAsync();
        await SeedGroupAsync(boards);
        await boards.App.Services.GetRequiredService<Contactor.Web.Features.Relays.RelayControl>()
            .TurnOnAsync(await RelayIdAsync(boards, 1), John, Cancel);
        using var page = await PageRenderer.StartAsync<BoardPage>(boards.App.Services, John, "John", new Dictionary<string, object?> { ["Id"] = boards.BarnId });

        await page.ClickAsync(PageRenderer.With("class", "remove"));
        Assert.True(await page.HasAsync(PageRenderer.With("role", "alertdialog")));
        var text = await page.TextAsync();
        Assert.Contains("Remove Barn?", text, StringComparison.Ordinal);
        Assert.Contains("This deletes, and can't be undone:", text, StringComparison.Ordinal);
        Assert.Contains("4 relays", text, StringComparison.Ordinal);
        Assert.Contains("1 group", text, StringComparison.Ordinal);
        Assert.Contains("1 schedule", text, StringComparison.Ordinal);
        Assert.Contains("1 input action", text, StringComparison.Ordinal);
        Assert.Contains("All of Barn's events", text, StringComparison.Ordinal);
        Assert.Contains("Its relays are turned off first.", text, StringComparison.Ordinal);

        await page.ClickAsync(PageRenderer.With("class", "cancel"));
        Assert.False(await page.HasAsync(PageRenderer.With("role", "alertdialog")));
        Assert.True(boards.Barn.State.IsOn(1));
        Assert.NotNull(boards.App.Services.GetRequiredService<BoardRegistry>().Find(boards.BarnId));

        await page.ClickAsync(PageRenderer.With("class", "remove"));
        await page.ClickAsync(PageRenderer.With("class", "confirm"));

        Assert.False(boards.Barn.State.IsOn(1));
        Assert.Null(boards.App.Services.GetRequiredService<BoardRegistry>().Find(boards.BarnId));
        var navigation = Assert.IsType<NavigationException>(Assert.Single(page.Errors));
        Assert.Equal("http://localhost/settings", navigation.Location);
    }

    [Fact]
    public async Task Should_WarnThatRelaysStayOn_WhenTheBoardHasNoContact()
    {
        var lost = FakeRelayBoard.LostContact(relayCount: 4, inputCount: 2);
        await using var boards = await TwoBoards.StartAsync(lost);
        using var page = await PageRenderer.StartAsync<BoardPage>(boards.App.Services, John, "John", new Dictionary<string, object?> { ["Id"] = boards.BarnId });

        await page.ClickAsync(PageRenderer.With("class", "remove"));

        var text = await page.TextAsync();
        Assert.Contains("Barn has no contact. Any relay that's on stays on, and Contactor can't turn it off.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Its relays are turned off first.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("group", text.Split("can't be undone:")[1].Split("All of")[0], StringComparison.Ordinal);

        await page.ClickAsync(PageRenderer.With("class", "confirm"));
        Assert.Null(boards.App.Services.GetRequiredService<BoardRegistry>().Find(boards.BarnId));
        Assert.Empty(lost.Sent);
    }

    // Barn's relays 3 → 4 as a group with a schedule, and input 1 turning relay 2 on.
    private static async Task SeedGroupAsync(TwoBoards boards)
    {
        await using var db = await boards.Factory.CreateDbContextAsync(Cancel);
        var relays = await db.Relays.Where(r => r.BoardId == boards.BarnId).ToDictionaryAsync(r => r.Channel, r => r.Id, Cancel);
        var input = await db.Inputs.Where(i => i.BoardId == boards.BarnId && i.Channel == 1).Select(i => i.Id).SingleAsync(Cancel);
        var group = new RelayGroup { BoardId = boards.BarnId, Name = "Heater", SortOrder = 1 };
        group.Members.Add(new RelayGroupMember { RelayId = relays[3], Position = 1, RoleName = "Fan" });
        group.Members.Add(new RelayGroupMember { RelayId = relays[4], Position = 2, RoleName = "Heat" });
        db.RelayGroups.Add(group);
        await db.SaveChangesAsync(Cancel);
        db.Schedules.Add(new Schedule { GroupId = group.Id, Days = ScheduleDays.All, OnTime = new(6, 0), OffTime = new(7, 0), CreatedBy = John });
        db.InputActions.Add(new InputAction { InputId = input, RelayId = relays[2], WhenClosed = true });
        await db.SaveChangesAsync(Cancel);
    }

    private static async Task<int> RelayIdAsync(TwoBoards boards, int channel)
    {
        await using var db = await boards.Factory.CreateDbContextAsync(Cancel);
        return await db.Relays.Where(r => r.BoardId == boards.BarnId && r.Channel == channel).Select(r => r.Id).SingleAsync(Cancel);
    }
}
