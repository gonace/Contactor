using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>Relay groups on the screens, as the server first renders them, with no board connected.</summary>
public sealed class GroupPagesTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Before the board's first answer the group's card and its screen are grey, with a "–" for every button.
    [Fact]
    public async Task Should_ShowTheGroupAsUnknown_BeforeTheBoardsFirstAnswer()
    {
        await using var app = CreateApp(answered: false);
        using var http = app.CreateClient();
        await http.GetStringAsync("/", Cancel);
        var id = await AddFanHeaterAsync(app);

        var list = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));
        var screen = WebUtility.HtmlDecode(await http.GetStringAsync($"/group/{id}", Cancel));

        Assert.Contains("aria-label=\"Fan heater – Lathe: Waiting for the board\"", list, StringComparison.Ordinal);
        Assert.Contains("<section class=\"status unknown\"", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("TURN ON THE GROUP", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("Needs Fan to be on", screen, StringComparison.Ordinal);
        Assert.Equal(2, screen.Split("class=\"member-toggle unknown\"").Length - 1);
        Assert.Equal(2, Regex.Count(screen, "<li class=\"step +unknown\""));
    }

    [Fact]
    public async Task Should_SendTheRelayPageToItsGroup()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient();
        await http.GetStringAsync("/", Cancel);
        var id = await AddFanHeaterAsync(app);

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/relay/2", Cancel));

        Assert.Contains("This relay is in the group", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"group/{id}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("TURN ON", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Active</strong>", html, StringComparison.Ordinal);
    }

    // A board that answered and then lost contact, unless asked for one that never answered.
    private static TestWebApp CreateApp(bool answered = true)
    {
        return new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = "john@example.com" },
            answered ? TestWebApp.WithLostBoard : null);
    }

    // Straight into the database: setting up through GroupsHandler needs a board to see the relays are off.
    private static async Task<int> AddFanHeaterAsync(TestWebApp app)
    {
        var factory = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        await using var db = await factory.CreateDbContextAsync(Cancel);
        var group = new RelayGroup { BoardId = 1, Name = "Fan heater – Lathe", Icon = GroupIcons.Fan, MaxOnMinutes = 120 };
        group.AddStep("Fan", 1, runOnSec: 300);
        group.AddStep("Heat", 2);
        await db.AddWithRuntimesAsync(group);
        return group.Id;
    }
}
