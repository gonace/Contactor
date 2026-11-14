using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace Contactor.Tests.Simulator;

/// <summary>The simulator's page, as the server first draws it.</summary>
public sealed class SimulatorPageTests
{
    [Fact]
    public async Task Should_ShowTheBoardFromTheSettings_WaitingForTheApp()
    {
        var port = SimulatedBoardsTests.FreePort();
        await using var app = new WebApplicationFactory<Contactor.Simulator.Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.UseSetting("Simulator:Boards:0:Type", "vm201");
            builder.UseSetting("Simulator:Boards:0:Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Simulator:Boards:0:Username", "john");
            builder.UseSetting("Simulator:Boards:0:Password", "secret");
        });
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", TestContext.Current.CancellationToken));

        Assert.Matches("<h1[^>]*>Contactor simulator</h1>", html);
        Assert.Contains("1 board", html, StringComparison.Ordinal);
        Assert.Contains("Point the app at 127.0.0.1 and the board's port", html, StringComparison.Ordinal);
        Assert.Contains($"VM201 · 127.0.0.1:{port}", html, StringComparison.Ordinal);
        Assert.Contains($"Velleman VM201 · 127.0.0.1:{port}", html, StringComparison.Ordinal);
        Assert.Contains("Waiting for the app · login john", html, StringComparison.Ordinal);
        Assert.Contains("from the settings", html, StringComparison.Ordinal);
        Assert.Contains($"Nothing yet. Set the app's board to 127.0.0.1, port {port}, user john.", html, StringComparison.Ordinal);
        Assert.Contains("RELAY 8", html, StringComparison.Ordinal);
        Assert.Contains("Board's own timers on", html, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", html, StringComparison.Ordinal);

        // A new board's login in two fields, so either may hold ':'.
        Assert.Matches("<label for=\"username\"[^>]*>User name</label>", html);
        Assert.Matches("<label for=\"password\"[^>]*>Password</label>", html);
        Assert.DoesNotContain("user:password", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ShowTheAddressTheAppReachesTheBoardsAt_FromTheSettings()
    {
        var port = SimulatedBoardsTests.FreePort();
        await using var app = CreateApp(port, appAddress: " host.docker.internal ");
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", TestContext.Current.CancellationToken));

        Assert.Contains("Point the app at host.docker.internal and the board's port", html, StringComparison.Ordinal);
        Assert.Contains($"VM201 · host.docker.internal:{port}", html, StringComparison.Ordinal);
        Assert.Contains($"Velleman VM201 · host.docker.internal:{port}", html, StringComparison.Ordinal);
        Assert.Contains($"Nothing yet. Set the app's board to host.docker.internal, port {port}.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_Show127001_WhenTheAddressIsEmpty()
    {
        var port = SimulatedBoardsTests.FreePort();
        await using var app = CreateApp(port, appAddress: "  ");
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", TestContext.Current.CancellationToken));

        Assert.Contains($"Velleman VM201 · 127.0.0.1:{port}", html, StringComparison.Ordinal);
        Assert.Contains($"Set the app's board to 127.0.0.1, port {port}.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ShowAnIPv6AddressInBrackets()
    {
        var port = SimulatedBoardsTests.FreePort();
        await using var app = CreateApp(port, appAddress: "fd00::1");
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", TestContext.Current.CancellationToken));

        Assert.Contains($"Velleman VM201 · [fd00::1]:{port}", html, StringComparison.Ordinal);
        Assert.Contains($"Set the app's board to fd00::1, port {port}.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_FoldAddABoardAndTheTraffic_OnlyBelow1024Px()
    {
        var port = SimulatedBoardsTests.FreePort();
        await using var app = CreateApp(port);
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", TestContext.Current.CancellationToken));
        var css = Regex.Replace(
            await http.GetStringAsync("/Contactor.Simulator.styles.css", TestContext.Current.CancellationToken), @"/\*.*?\*/", "", RegexOptions.Singleline);

        // Both folds start closed, and say which part they show.
        Assert.Matches("<button type=\"button\" class=\"fold add-fold\" aria-expanded=\"false\" aria-controls=\"add\"[^>]*>\\+ Add a board</button>", html);
        Assert.Matches("<button type=\"button\" class=\"fold traffic-fold\" aria-expanded=\"false\" aria-controls=\"traffic\"[^>]*>Traffic · newest first</button>", html);

        // Still drawn in full, so nothing is gone at 1024 px and wider.
        Assert.Matches("<form id=\"add\" class=\"add ?\"", html);
        Assert.Matches("<section id=\"traffic\" class=\"card traffic ?\"", html);
        Assert.Contains("Add board</button>", html, StringComparison.Ordinal);
        Assert.Contains("TIME", html, StringComparison.Ordinal);

        // Only the folds' own buttons are hidden at 1024 px and wider; everything else is hidden only below it.
        var narrow = css.IndexOf("@media (max-width: 1023.98px)", StringComparison.Ordinal);
        Assert.True(narrow > 0, "The page has no layout below 1024 px.");
        var hiddenWide = Regex.Matches(css[..narrow], @"([^{}]+)\{[^{}]*display:\s*none")
            .Select(m => m.Groups[1].Value.Trim())
            .ToList();
        Assert.Equal([".fold"], hiddenWide.Select(selector => Regex.Replace(selector, @"\[b-[a-z0-9]+\]", "")));
        Assert.DoesNotContain("min-width: 1000px", css, StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Contactor.Simulator.Program> CreateApp(int port, string? appAddress = null)
    {
        return new WebApplicationFactory<Contactor.Simulator.Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.UseSetting("Simulator:Boards:0:Type", "vm201");
            builder.UseSetting("Simulator:Boards:0:Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (appAddress is not null)
            {
                builder.UseSetting("Simulator:AppAddress", appAddress);
            }
        });
    }
}
