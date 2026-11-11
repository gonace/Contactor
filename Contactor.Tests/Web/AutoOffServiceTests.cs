using Contactor.Boards.Vm201;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Contactor.Tests.Web;

public sealed class AutoOffServiceTests
{
    private const string Jane = "jane@example.com";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_TurnOffAndLog_WhenTheTimeHasPassed()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await rig.Control.TurnOnAsync(3, Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(3));

        rig.Time.Now += TimeSpan.FromMinutes(Relay.DefaultMaxOnMinutes);
        var turnedOff = await AutoOff(rig).CheckAsync(Cancel);

        Assert.Equal([3], turnedOff);
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsOn(3));
        await rig.WaitForEventsAsync(events => events.Count == 2);
        var off = (await rig.EventsAsync())[1];
        Assert.Equal((EventActors.AutoOff, EventActions.Off, 3, """{"afterMinutes":120}"""), (off.Actor, off.Action, off.RelayId!.Value, off.Details));
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        var runtime = await db.RelayRuntimes.SingleAsync(r => r.RelayId == 3, Cancel);
        Assert.Equal((null, null, null), (runtime.OnSinceUtc, runtime.AutoOffAtUtc, runtime.TurnedOnBy));
    }

    [Fact]
    public async Task Should_NotLogTheBoardsAnswerAsExternal()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await rig.Control.TurnOnAsync(3, Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(3));
        rig.Time.Now += TimeSpan.FromMinutes(Relay.DefaultMaxOnMinutes);

        await AutoOff(rig).CheckAsync(Cancel);
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsOn(3));
        await rig.Client.RequestStatusAsync(Cancel);
        await Task.Delay(200, Cancel);

        Assert.DoesNotContain(await rig.EventsAsync(), e => e.Actor == EventActors.External);
    }

    [Fact]
    public async Task Should_WaitUntilTheTime()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await rig.Control.TurnOnAsync(3, Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(3));

        rig.Time.Now += TimeSpan.FromMinutes(Relay.DefaultMaxOnMinutes) - TimeSpan.FromSeconds(1);

        Assert.Empty(await AutoOff(rig).CheckAsync(Cancel));
        Assert.True(rig.Client.State.IsOn(3));
    }

    [Fact]
    public async Task Should_OnlyForgetThePlan_WhenTheRelayIsAlreadyOff()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            db.RelayRuntimes.Update(new RelayRuntime
            {
                RelayId = 2,
                OnSinceUtc = BoardTestRig.Start.AddHours(-3),
                AutoOffAtUtc = BoardTestRig.Start.AddHours(-1),
                TurnedOnBy = Jane,
            });
            await db.SaveChangesAsync(Cancel);
        }

        Assert.Empty(await AutoOff(rig).CheckAsync(Cancel));

        Assert.DoesNotContain(rig.Simulator.ReceivedCommands, p => p.Command == Vm201Command.Off);
        Assert.Empty(await rig.EventsAsync());
        await using var check = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        Assert.Null((await check.RelayRuntimes.SingleAsync(r => r.RelayId == 2, Cancel)).AutoOffAtUtc);
    }

    [Fact]
    public async Task Should_WaitForTheBoard_WhenDisconnected()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await rig.Control.TurnOnAsync(3, Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(3));
        rig.Simulator.RejectConnections = true;
        rig.Simulator.DropConnections();
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsConnected);
        rig.Time.Now += TimeSpan.FromHours(3);

        Assert.Empty(await AutoOff(rig).CheckAsync(Cancel));
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            Assert.NotNull((await db.RelayRuntimes.SingleAsync(r => r.RelayId == 3, Cancel)).AutoOffAtUtc);
        }

        // Back after a while: the passed time is handled at the next check.
        rig.Simulator.RejectConnections = false;
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsConnected);
        Assert.Equal([3], await AutoOff(rig).CheckAsync(Cancel));
    }

    [Fact]
    public async Task Should_LeaveRelaysWithoutALimitOn()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            (await db.Relays.SingleAsync(r => r.Id == 4, Cancel)).MaxOnMinutes = null;
            await db.SaveChangesAsync(Cancel);
        }

        await rig.Control.TurnOnAsync(4, Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(4));
        rig.Time.Now += TimeSpan.FromDays(2);

        Assert.Empty(await AutoOff(rig).CheckAsync(Cancel));
    }

    private static AutoOffService AutoOff(BoardTestRig rig)
    {
        return rig.Database.Services.GetRequiredService<AutoOffService>();
    }
}
