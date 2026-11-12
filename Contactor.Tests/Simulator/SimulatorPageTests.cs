using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

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
            builder.UseSetting("Simulator:Boards:0:Type", "vm201");
            builder.UseSetting("Simulator:Boards:0:Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Simulator:Boards:0:Username", "john");
            builder.UseSetting("Simulator:Boards:0:Password", "secret");
        });
        using var http = app.CreateClient();

        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/", TestContext.Current.CancellationToken));

        Assert.Matches("<h1[^>]*>Contactor simulator</h1>", html);
        Assert.Contains("1 board · listening on 127.0.0.1", html, StringComparison.Ordinal);
        Assert.Contains($"VM201 · {port}", html, StringComparison.Ordinal);
        Assert.Contains($"Velleman VM201 · port {port}", html, StringComparison.Ordinal);
        Assert.Contains("Waiting for the app · login john", html, StringComparison.Ordinal);
        Assert.Contains("from the settings", html, StringComparison.Ordinal);
        Assert.Contains($"Nothing yet. Set the app's board to 127.0.0.1, port {port}, user john.", html, StringComparison.Ordinal);
        Assert.Contains("RELAY 8", html, StringComparison.Ordinal);
        Assert.Contains("Board's own timers on", html, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", html, StringComparison.Ordinal);

        // A new board's login in two fields, so either may hold ':'.
        Assert.Contains("<label for=\"username\">User name</label>", html, StringComparison.Ordinal);
        Assert.Contains("<label for=\"password\">Password</label>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("user:password", html, StringComparison.Ordinal);
    }
}
