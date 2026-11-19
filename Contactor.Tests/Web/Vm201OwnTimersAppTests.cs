using Contactor.Boards.Velleman;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// The VM201's own timers in the whole app: they are the board's own rules, which change only on a person's press
/// (issue #497). The app no longer turns them off at its first connection.
/// </summary>
public sealed class Vm201OwnTimersAppTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Every service of the app runs against a VM201 with its own timers on for relays 3 and 5, on the app's clock: the
    // first connection, keep-alives for 10 minutes and a reconnect leave the timers as they are, and nothing asks the
    // board to change them. The setting an installation from before issue #497 may still have (Vm201__DisableBoardTimers
    // in its .env) is ignored: the app starts with it, and it turns nothing off.
    [Fact]
    public async Task Should_NeverChangeTheVm201sTimers_WithoutAPress_InTheWholeApp()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await simulator.SetTimerMaskAsync(0x14);
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?>
            {
                ["Vm201:Host"] = "127.0.0.1",
                ["Vm201:Port"] = simulator.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Vm201:DisableBoardTimers"] = "true",
            },
            services => services.AddSingleton<TimeProvider>(time));
        var client = app.Services.BoardService<Vm201Client>();

        await Wait.UntilAsync(() => client.State.IsConnected);
        await Wait.UntilAsync(() => Found(app) == 2);

        var start = time.GetUtcNow();
        while (time.GetUtcNow() - start < TimeSpan.FromMinutes(10))
        {
            await OwnRulesServiceTests.KeepAliveAsync(simulator, time);
        }

        simulator.DropConnections();
        await Wait.UntilAsync(() =>
        {
            if (simulator.ConnectionCount >= 2 && client.State.IsConnected)
            {
                return true;
            }

            // The client waits for its next attempt on the app's clock.
            time.Advance(TimeSpan.FromSeconds(1));
            return false;
        });
        await OwnRulesServiceTests.KeepAliveAsync(simulator, time);
        await Wait.UntilAsync(() => client.State.TimerMask == 0x14);

        Assert.Equal(0x14, simulator.TimerMask);
        Assert.DoesNotContain(simulator.ReceivedCommands, p => p.Command is Vm201Command.TimerDisable or Vm201Command.TimerEnable or Vm201Command.TimerToggle);
        Assert.Equal(2, Found(app));
        Assert.Equal(0, Count(app, EventActions.BoardTimersDisabled));
    }

    // The VM201 brings nothing of its own beside its connection: no service that runs while it is the board.
    [Fact]
    public async Task Should_BuildTheVm201_WithNothingOfItsOwn()
    {
        await using var app = new TestWebApp("Development");

        var board = app.Services.Live().Active;

        Assert.NotNull(board);
        Assert.IsType<Vm201Board>(board.Board);
        Assert.Empty(board.Extras);
    }

    private static int Found(TestWebApp app)
    {
        return Count(app, EventActions.BoardOwnRuleFound);
    }

    private static int Count(TestWebApp app, string action)
    {
        using var db = app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContext();
        return db.EventLog.Count(e => e.Action == action);
    }
}
