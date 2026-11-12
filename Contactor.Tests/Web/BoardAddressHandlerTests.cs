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

    // Another board may be at the new address: who turned the old board's relays and groups on, and their auto-off,
    // must not apply to it. The same address keeps them.
    [Fact]
    public async Task Should_ClearTheOldBoardsRuns_AtANewAddressOnly()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await using var other = new Vm201Simulator();
        other.Start();
        int groupId;
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            var relay = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 3, Cancel);
            (relay.OnSinceUtc, relay.AutoOffAtUtc, relay.TurnedOnBy) = (BoardTestRig.Start, BoardTestRig.Start.AddHours(2), John);
            var group = new RelayGroup { Name = "Fan heater", Icon = GroupIcons.Fan };
            group.Members.Add(new RelayGroupMember { RelayId = 1, Position = 1, RoleName = "Fan" });
            group.Members.Add(new RelayGroupMember { RelayId = 2, Position = 2, RoleName = "Heat" });
            db.RelayGroups.Add(group);
            await db.SaveChangesAsync(Cancel);
            groupId = group.Id;
            db.GroupRuntimes.Add(new GroupRuntime
            {
                GroupId = groupId,
                Phase = GroupPhases.Stopping,
                NextStepAtUtc = BoardTestRig.Start.AddMinutes(2),
                PendingPosition = 1,
                OnSinceUtc = BoardTestRig.Start,
                AutoOffAtUtc = BoardTestRig.Start.AddHours(2),
                TurnedOnBy = John,
            });
            await db.SaveChangesAsync(Cancel);
        }

        Assert.Null(await Handler(rig).SaveAsync("127.0.0.1", rig.Simulator.Port, John, Cancel));
        Assert.Equal(John, (await RuntimesAsync(rig)).Relay.TurnedOnBy);

        Assert.Null(await Handler(rig).SaveAsync("127.0.0.1", other.Port, John, Cancel));

        var (relayAfter, groupAfter) = await RuntimesAsync(rig);
        Assert.Equal(((DateTime?)null, (DateTime?)null, (string?)null), (relayAfter.OnSinceUtc, relayAfter.AutoOffAtUtc, relayAfter.TurnedOnBy));
        Assert.Equal((GroupPhases.Idle, (int?)null, (DateTime?)null, (string?)null), (groupAfter.Phase, groupAfter.PendingPosition, groupAfter.AutoOffAtUtc, groupAfter.TurnedOnBy));

        async Task<(RelayRuntime Relay, GroupRuntime Group)> RuntimesAsync(BoardTestRig r)
        {
            await using var db = await r.Database.Factory.CreateDbContextAsync(Cancel);
            return (await db.RelayRuntimes.AsNoTracking().SingleAsync(x => x.RelayId == 3, Cancel),
                await db.GroupRuntimes.AsNoTracking().SingleAsync(x => x.GroupId == groupId, Cancel));
        }
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
