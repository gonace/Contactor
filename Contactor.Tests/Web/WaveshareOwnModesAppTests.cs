using System.Globalization;
using Contactor.Boards;
using Contactor.Boards.Waveshare;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

/// <summary>
/// The Waveshare (B)'s relay control modes in the whole app: they are the board's own rules, which change only on a
/// person's press (issue #497). The app no longer sets them to normal at each connection.
/// </summary>
public sealed class WaveshareOwnModesAppTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Every service of the app runs against a (B) with relay 3 in linkage and relay 5 in toggle mode, on the app's
    // clock: the first connection, three minutes of polls (with the modes read again each minute) and a reconnect leave
    // the modes as they are, and nothing writes a mode register (FC 06 or FC 10 at 0x1000–0x1007). The setting an
    // installation from before issue #497 may still have (Waveshare__ResetRelayModes in its .env) is ignored.
    [Fact]
    public async Task Should_NeverChangeTheModes_WithoutAPress_InTheWholeApp()
    {
        await using var simulator = new WaveshareSimulator(WaveshareModels.Relay8B);
        simulator.Start();
        await simulator.SetRelayModeAsync(3, WaveshareApi.ModeLinkage);
        await simulator.SetRelayModeAsync(5, WaveshareApi.ModeToggle);
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?>
            {
                ["Board:Type"] = "waveshare-8b",
                ["Waveshare:Host"] = "127.0.0.1",
                ["Waveshare:Port"] = simulator.Port.ToString(CultureInfo.InvariantCulture),
                ["Waveshare:ResetRelayModes"] = "true",
            },
            services => services.AddSingleton<TimeProvider>(time));
        var client = app.Services.BoardService<WaveshareClient>();

        await Wait.UntilAsync(() => client.State.IsConnected);
        await Wait.UntilAsync(() => Found(app) == 2);
        Assert.Equal(ChannelMask.Of(3), app.Services.Live().State.Unavailable);

        var start = time.GetUtcNow();
        while (time.GetUtcNow() - start < TimeSpan.FromMinutes(3))
        {
            await PollAsync(simulator, time);
        }

        Assert.True(simulator.ReceivedRequests.Count(r => r.StartsWith("01 03 10 00", StringComparison.Ordinal)) >= 3);

        // A dropped connection: the client may be on a new one at once (a request under way goes once more), so only
        // its next read of the modes, on that new connection, is waited for.
        var reads = simulator.ReceivedRequests.Count(r => r.StartsWith("01 03 10 00", StringComparison.Ordinal));
        simulator.DropConnections();
        await Wait.UntilAsync(() =>
        {
            if (client.State.IsConnected && simulator.ReceivedRequests.Count(r => r.StartsWith("01 03 10 00", StringComparison.Ordinal)) > reads)
            {
                return true;
            }

            // The client waits for its next poll or attempt on the app's clock.
            time.Advance(TimeSpan.FromSeconds(1));
            return false;
        });

        Assert.Equal(new ushort[] { 0, 0, 1, 0, 2, 0, 0, 0 }, simulator.RelayModes);
        Assert.DoesNotContain(simulator.ReceivedRequests, r => r.StartsWith("01 06 10 0", StringComparison.Ordinal) || r.StartsWith("01 10 10 0", StringComparison.Ordinal));
        Assert.Equal(2, Found(app));
        Assert.Equal(0, Count(app, EventActions.BoardRelayModesReset) + Count(app, EventActions.BoardRelayModesNotReset));
    }

    // The Waveshare brings nothing of its own beside its connection, and its relays and inputs are made when chosen.
    [Theory]
    [InlineData("waveshare-8", 8, 0)]
    [InlineData("waveshare-8b", 8, 8)]
    [InlineData("waveshare-30", 30, 0)]
    public async Task Should_BuildTheBoardWithNothingOfItsOwn_AndSeedItsRelaysAndInputs(string type, int relays, int inputs)
    {
        await using var simulator = new WaveshareSimulator(WaveshareModels.All.Single(m => m.Id == type));
        simulator.Start();
        await using var app = new TestWebApp(
            "Development",
            new Dictionary<string, string?>
            {
                ["Board:Type"] = type,
                ["Waveshare:Host"] = "127.0.0.1",
                ["Waveshare:Port"] = simulator.Port.ToString(CultureInfo.InvariantCulture),
            });

        var board = app.Services.Live().Active;

        Assert.NotNull(board);
        Assert.IsType<WaveshareBoard>(board.Board);
        Assert.Empty(board.Extras);
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel);
        Assert.Equal(relays, await db.Relays.CountAsync(Cancel));
        Assert.Equal(inputs, await db.Inputs.CountAsync(Cancel));
    }

    // The app's clock moves on until the board gets its next request: the client's next poll.
    private static async Task PollAsync(WaveshareSimulator simulator, ManualTimeProvider time)
    {
        var requests = simulator.RequestCount;
        await Wait.UntilAsync(() =>
        {
            if (simulator.RequestCount > requests)
            {
                return true;
            }

            time.Advance(TimeSpan.FromSeconds(1));
            return false;
        });
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
