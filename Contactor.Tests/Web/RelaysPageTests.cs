using System.Net;
using Contactor.Web.Components;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Relays;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// The relay screen as the server first renders it, with no board connected, and a live one closed while it loads.
/// </summary>
public sealed class RelaysPageTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_WarnAndDisableButtons_WhenTheBoardIsNotConnected()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = "john@example.com" },
            TestWebApp.WithLostBoard);
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        Assert.Contains("No contact with the relay board", html, StringComparison.Ordinal);
        Assert.Contains("The states below may be wrong.", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Turn on Relay 1\"", html, StringComparison.Ordinal);
        Assert.Equal(9, CountOccurrences(html, "disabled"));
    }

    // Before the board's first answer nothing is known: every card is grey with a "–", and nothing counts as on.
    [Fact]
    public async Task Should_ShowEveryStateAsUnknown_BeforeTheBoardsFirstAnswer()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = "john@example.com" });
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", Cancel));

        Assert.Contains("No contact with the relay board yet", html, StringComparison.Ordinal);
        Assert.Contains("Waiting for its first answer.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("The states below may be wrong.", html, StringComparison.Ordinal);
        Assert.DoesNotContain(" on</strong>", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">OFF<", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">Open<", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Relay 1: Waiting for the board\"", html, StringComparison.Ordinal);
        Assert.Equal(8, CountOccurrences(html, "class=\"toggle unknown\""));
        Assert.Equal(1, CountOccurrences(html, "class=\"state unknown\""));

        // The same on the screens of a relay and an input.
        var relay = WebUtility.HtmlDecode(await http.GetStringAsync("/relay/3", Cancel));
        var input = WebUtility.HtmlDecode(await http.GetStringAsync("/input/1", Cancel));
        Assert.Contains("<section class=\"status unknown\"", relay, StringComparison.Ordinal);
        Assert.DoesNotContain("TURN ON", relay, StringComparison.Ordinal);
        Assert.Contains("Waiting for the board", input, StringComparison.Ordinal);
        Assert.Contains("class=\"state unknown\"", input, StringComparison.Ordinal);
    }

    // A page closed while its first load is under way: when the load ends, it must not subscribe, or its handlers stay
    // for the life of the app, holding the closed page and running on every board update.
    [Fact]
    public async Task Should_LeaveNoHandlers_WhenThePageClosesWhileItLoads()
    {
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?> { [TestWebApp.SignedInAs] = "john@example.com" },
            TestWebApp.WithLostBoard);
        var registry = app.Services.GetRequiredService<BoardRegistry>();
        var changes = app.Services.GetRequiredService<RelayChanges>();
        var signIn = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);

        var (page, rendered) = PageRenderer.StartHeld<Relays>(app.Services, signIn.Task);
        page.Close();
        signIn.SetResult(PageRenderer.SignedIn("john@example.com", "John"));
        await rendered.WaitAsync(Cancel);

        Assert.Equal(0, PageRenderer.Handlers<Relays>(registry, nameof(BoardRegistry.StateChanged)));
        Assert.Equal(0, PageRenderer.Handlers<Relays>(registry, nameof(BoardRegistry.AnalogChanged)));
        Assert.Equal(0, PageRenderer.Handlers<Relays>(registry, nameof(BoardRegistry.BoardsChanged)));
        Assert.Equal(0, PageRenderer.Handlers<Relays>(changes, nameof(RelayChanges.Changed)));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
