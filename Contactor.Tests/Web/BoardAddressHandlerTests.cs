using Contactor.Boards;
using Contactor.Boards.Vm201;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

public sealed class BoardAddressHandlerTests
{
    private const string John = "john@example.com";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_UseTheSettings_UntilAnAddressIsSaved()
    {
        await using var rig = await BoardTestRig.StartAsync();

        var endpoint = await Handler(rig).GetAsync(Cancel);

        Assert.Equal(new BoardEndpoint("127.0.0.1", rig.Simulator.Port), endpoint);
    }

    [Fact]
    public async Task Should_SaveLogAndMoveTheClient()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await using var other = new Vm201Simulator();
        other.Start();
        var from = $"127.0.0.1:{rig.Simulator.Port}";

        Assert.Null(await Handler(rig).SaveAsync(" 127.0.0.1 ", other.Port, John, Cancel));

        await BoardTestRig.WaitUntilAsync(() => other.ConnectionCount == 1 && rig.Client.State.IsConnected);
        Assert.Equal(new BoardEndpoint("127.0.0.1", other.Port), await Handler(rig).GetAsync(Cancel));
        var entry = Assert.Single(await rig.EventsAsync(), e => e.Action == EventActions.BoardAddressChanged);
        Assert.Equal(John, entry.Actor);
        Assert.Equal($"changed the relay board's address from {from} to 127.0.0.1:{other.Port}", EventText.For(entry, null, "John").What);
    }

    // The app can't tell a new board from the same board at a new IP, so a relay's run is kept: for the same board
    // clearing it would leave a fan heater without its auto-off.
    [Fact]
    public async Task Should_KeepTheRelaysRuns_AtANewAddress()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await using var other = new Vm201Simulator();
        other.Start();
        var autoOff = BoardTestRig.Start.AddHours(2);
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            var relay = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 3, Cancel);
            (relay.OnSinceUtc, relay.AutoOffAtUtc, relay.TurnedOnBy) = (BoardTestRig.Start, autoOff, John);
            await db.SaveChangesAsync(Cancel);
        }

        Assert.Null(await Handler(rig).SaveAsync("127.0.0.1", other.Port, John, Cancel));

        await using var check = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        var after = await check.RelayRuntimes.AsNoTracking().SingleAsync(r => r.RelayId == 3, Cancel);
        Assert.Equal(((DateTime?)autoOff, John), (after.AutoOffAtUtc, after.TurnedOnBy));
    }

    [Fact]
    public async Task Should_LogNothing_WhenTheAddressIsTheSame()
    {
        await using var rig = await BoardTestRig.StartAsync();

        Assert.Null(await Handler(rig).SaveAsync("127.0.0.1", rig.Simulator.Port, John, Cancel));
        Assert.Null(await Handler(rig).SaveAsync("127.0.0.1", rig.Simulator.Port, John, Cancel));

        Assert.DoesNotContain(await rig.EventsAsync(), e => e.Action == EventActions.BoardAddressChanged);
        Assert.Equal(1, rig.Simulator.ConnectionCount);
    }

    [Theory]
    [InlineData("", 9760)]
    [InlineData("192.168.1.50", 0)]
    [InlineData("not an address", 9760)]
    public async Task Should_SaveNothing_ForAWrongAddress(string host, int port)
    {
        await using var rig = await BoardTestRig.StartAsync();

        Assert.NotNull(await Handler(rig).SaveAsync(host, port, John, Cancel));

        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        Assert.Empty(await db.BoardSettings.ToListAsync(Cancel));
        Assert.Equal(rig.Simulator.Port, rig.Client.Endpoint.Port);
    }

    [Fact]
    public async Task Should_UseTheSavedAddress_AfterARestart()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            db.BoardSettings.Add(new BoardSetting { Host = "192.168.1.51", Port = 9761 });
            await db.SaveChangesAsync(Cancel);
        }

        await Handler(rig).ApplySavedAsync(Cancel);

        Assert.Equal(new BoardEndpoint("192.168.1.51", 9761), rig.Client.Endpoint);
    }

    private static BoardAddressHandler Handler(BoardTestRig rig)
    {
        return rig.Database.Services.GetRequiredService<BoardAddressHandler>();
    }
}
