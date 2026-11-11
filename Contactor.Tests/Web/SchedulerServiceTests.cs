using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Contactor.Vm201;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>
/// The scheduler against the simulator. The rig starts on Wednesday 4 November 2026 at 13:00 UTC, 14:00 local.
/// </summary>
public sealed class SchedulerServiceTests
{
    private const string Jane = "jane@example.com";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_TurnOnAtOnTimeAndOffAtOffTime()
    {
        await using var rig = await BoardTestRig.StartAsync();
        var id = await AddScheduleAsync(rig, relayId: 2, 14, 5, 14, 10);
        await rig.Scheduler.TickAsync(Cancel);

        await TickAtAsync(rig, 14, 4);
        Assert.False(rig.Client.State.IsOn(2));

        await TickAtAsync(rig, 14, 5);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(2));
        var runtime = await RuntimeAsync(rig, 2);
        Assert.Equal((SchedulerActor, (DateTime?)null), (runtime.TurnedOnBy, runtime.AutoOffAtUtc));

        await TickAtAsync(rig, 14, 10);
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsOn(2));

        await rig.WaitForEventsAsync(events => events.Count == 2);
        var events = await rig.EventsAsync();
        Assert.All(events, e => Assert.Equal((EventActors.Schedule, 2, $$"""{"scheduleId":{{id}}}"""), (e.Actor, e.RelayId!.Value, e.Details)));
        Assert.Equal([EventActions.On, EventActions.Off], events.Select(e => e.Action));
        Assert.Null((await RuntimeAsync(rig, 2)).TurnedOnBy);
    }

    [Fact]
    public async Task Should_TurnOnARunningScheduleAtStart()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await AddScheduleAsync(rig, relayId: 1, 13, 0, 15, 0);

        await rig.Scheduler.TickAsync(Cancel);

        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(1));
    }

    [Fact]
    public async Task Should_NotTurnBackOn_WhenSomeoneTurnedItOffDuringTheRun()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await AddScheduleAsync(rig, relayId: 1, 14, 5, 15, 0);
        await rig.Scheduler.TickAsync(Cancel);
        await TickAtAsync(rig, 14, 5);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(1));

        await rig.Control.TurnOffAsync(1, Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsOn(1));
        await TickAtAsync(rig, 14, 6);
        await TickAtAsync(rig, 14, 7);

        Assert.False(rig.Client.State.IsOn(1));
    }

    [Fact]
    public async Task Should_LetOnWin_WhenSchedulesOverlap()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await AddScheduleAsync(rig, relayId: 3, 14, 5, 14, 10);
        await AddScheduleAsync(rig, relayId: 3, 14, 8, 14, 20);
        await rig.Scheduler.TickAsync(Cancel);
        await TickAtAsync(rig, 14, 5);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(3));

        await TickAtAsync(rig, 14, 10);
        await Task.Delay(200, Cancel);
        Assert.True(rig.Client.State.IsOn(3));

        await TickAtAsync(rig, 14, 20);
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsOn(3));
    }

    [Fact]
    public async Task Should_TurnOffAtOffTime_EvenWhenAPersonTurnedItOn()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await AddScheduleAsync(rig, relayId: 4, 14, 5, 14, 10);
        await rig.Scheduler.TickAsync(Cancel);
        await rig.Control.TurnOnAsync(4, Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(4));

        await TickAtAsync(rig, 14, 5);
        await TickAtAsync(rig, 14, 10);

        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsOn(4));
    }

    [Fact]
    public async Task Should_CatchUpAMissedOff_ForARelayTheScheduleTurnedOn()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await AddScheduleAsync(rig, relayId: 5, 14, 5, 14, 10);
        await rig.Scheduler.TickAsync(Cancel);
        await TickAtAsync(rig, 14, 5);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(5));

        await DisconnectAsync(rig);
        rig.Time.Now = Local(14, 30);
        await rig.Scheduler.TickAsync(Cancel);
        await ReconnectAsync(rig);

        await rig.Scheduler.TickAsync(Cancel);

        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsOn(5));
        Assert.Contains(await rig.EventsAsync(), e => e is { Actor: EventActors.Schedule, Action: EventActions.Off, RelayId: 5 });
    }

    [Fact]
    public async Task Should_NeverTurnOffAtStart_WhatAPersonTurnedOn()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await AddScheduleAsync(rig, relayId: 6, 14, 5, 14, 10);
        await rig.Scheduler.TickAsync(Cancel);
        await rig.Control.TurnOnAsync(6, Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(6));
        await TickAtAsync(rig, 14, 5);

        await DisconnectAsync(rig);
        rig.Time.Now = Local(14, 30);
        await ReconnectAsync(rig);
        await rig.Scheduler.TickAsync(Cancel);
        await Task.Delay(200, Cancel);

        Assert.True(rig.Client.State.IsOn(6));
    }

    [Fact]
    public async Task Should_TurnOnARunThatStartedWhileDisconnected()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await AddScheduleAsync(rig, relayId: 7, 14, 5, 15, 0);
        await rig.Scheduler.TickAsync(Cancel);

        await DisconnectAsync(rig);
        rig.Time.Now = Local(14, 20);
        await rig.Scheduler.TickAsync(Cancel);
        await ReconnectAsync(rig);
        await rig.Scheduler.TickAsync(Cancel);

        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(7));
    }

    [Fact]
    public async Task Should_NotTurnOnARunThatEndedWhileDisconnected()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await AddScheduleAsync(rig, relayId: 8, 14, 5, 14, 10);
        await rig.Scheduler.TickAsync(Cancel);

        await DisconnectAsync(rig);
        rig.Time.Now = Local(14, 30);
        await ReconnectAsync(rig);
        await rig.Scheduler.TickAsync(Cancel);
        await Task.Delay(200, Cancel);

        Assert.False(rig.Client.State.IsOn(8));
        Assert.DoesNotContain(await rig.EventsAsync(), e => e.Actor == EventActors.Schedule);
    }

    [Fact]
    public async Task Should_RememberTheLastMinute_OnlyWhileConnected()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await rig.Scheduler.TickAsync(Cancel);
        Assert.Equal(Local(14, 0).UtcDateTime, await LastTickAsync(rig));

        await DisconnectAsync(rig);
        rig.Time.Now = Local(14, 30);
        await rig.Scheduler.TickAsync(Cancel);

        Assert.Equal(Local(14, 0).UtcDateTime, await LastTickAsync(rig));
    }

    [Fact]
    public async Task Should_NotLogItsOwnCommandsAsExternal()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await AddScheduleAsync(rig, relayId: 2, 14, 5, 14, 10);
        await rig.Scheduler.TickAsync(Cancel);
        await TickAtAsync(rig, 14, 5);

        // The board's answer arrives before the clock moves on, as it does in real life, where the next tick is
        // 5 minutes later.
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(2));
        await TickAtAsync(rig, 14, 10);
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsOn(2));
        await rig.Client.RequestStatusAsync(Cancel);
        await Task.Delay(200, Cancel);

        Assert.DoesNotContain(await rig.EventsAsync(), e => e.Actor == EventActors.External);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_TurnOff_WhenTheRunningScheduleIsDeletedOrPaused(bool delete)
    {
        await using var rig = await BoardTestRig.StartAsync();
        var id = await AddScheduleAsync(rig, relayId: 1, 14, 5, 15, 0);
        await rig.Scheduler.TickAsync(Cancel);
        await TickAtAsync(rig, 14, 5);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(1));

        await using (var db = await rig.Database.Factory.CreateDbContextAsync(Cancel))
        {
            var schedule = await db.Schedules.SingleAsync(s => s.Id == id, Cancel);
            if (delete)
            {
                db.Schedules.Remove(schedule);
            }
            else
            {
                schedule.IsEnabled = false;
            }

            await db.SaveChangesAsync(Cancel);
        }

        await TickAtAsync(rig, 14, 6);

        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsOn(1));
        var off = (await rig.EventsAsync()).Last(e => e.Action == EventActions.Off);
        Assert.Equal((EventActors.Schedule, (string?)null), (off.Actor, off.Details));
        Assert.Null((await RuntimeAsync(rig, 1)).TurnedOnBy);
    }

    [Fact]
    public async Task Should_TurnOff_WhenItsOffIsOlderThanTheCatchUp()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await AddScheduleAsync(rig, relayId: 2, 14, 5, 14, 10);
        await rig.Scheduler.TickAsync(Cancel);
        await TickAtAsync(rig, 14, 5);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(2));

        // Two days away; the schedule's days are long past what the catch-up reaches, but today's run hasn't started.
        await DisconnectAsync(rig);
        rig.Time.Now = Local(14, 0).AddDays(2);
        await ReconnectAsync(rig);
        await rig.Scheduler.TickAsync(Cancel);

        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsOn(2));
    }

    [Fact]
    public async Task Should_LeaveAPersonsRelayOn_WhenNoScheduleCoversIt()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await rig.Control.TurnOnAsync(3, Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(3));

        await rig.Scheduler.TickAsync(Cancel);
        await TickAtAsync(rig, 14, 1);
        await Task.Delay(200, Cancel);

        Assert.True(rig.Client.State.IsOn(3));
    }

    [Fact]
    public async Task Should_LetAPersonsAutoOffEndTheRun_AndNotTurnItBackOn()
    {
        // The user's decision: Jane chose 2 h, so it goes off at 16:00 although a schedule runs 14:05–18:00.
        await using var rig = await BoardTestRig.StartAsync();
        await AddScheduleAsync(rig, relayId: 4, 14, 5, 18, 0);
        await rig.Scheduler.TickAsync(Cancel);
        await rig.Control.TurnOnAsync(4, Jane, Cancel);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(4));
        await TickAtAsync(rig, 14, 5);
        Assert.Equal(Jane, (await RuntimeAsync(rig, 4)).TurnedOnBy);

        rig.Time.Now = Local(16, 0);
        Assert.Equal([4], await AutoOff(rig).CheckAsync(Cancel));
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsOn(4));
        await TickAtAsync(rig, 16, 1);
        await TickAtAsync(rig, 17, 0);
        await Task.Delay(200, Cancel);

        Assert.False(rig.Client.State.IsOn(4));
    }

    [Fact]
    public async Task Should_KeepTheRun_WhenTheAutoOffLimitChanges()
    {
        await using var rig = await BoardTestRig.StartAsync();
        await AddScheduleAsync(rig, relayId: 5, 14, 5, 18, 0);
        await rig.Scheduler.TickAsync(Cancel);
        await TickAtAsync(rig, 14, 5);
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsOn(5));

        rig.Time.Now = Local(15, 10);
        var settings = rig.Database.Services.GetRequiredService<RelaySettingsHandler>();
        await settings.SaveAsync(new RelaySettings(5, "Relä 5", true, 60), Jane, Cancel);

        Assert.Empty(await AutoOff(rig).CheckAsync(Cancel));
        Assert.True(rig.Client.State.IsOn(5));
    }

    private static AutoOffService AutoOff(BoardTestRig rig)
    {
        return rig.Database.Services.GetRequiredService<AutoOffService>();
    }

    private const string SchedulerActor = EventActors.Schedule;

    // 14:00 local is 13:00 UTC on the rig's day.
    private static DateTimeOffset Local(int hour, int minute)
    {
        return new DateTimeOffset(2026, 11, 4, hour - 1, minute, 0, TimeSpan.Zero);
    }

    private static async Task TickAtAsync(BoardTestRig rig, int hour, int minute)
    {
        rig.Time.Now = Local(hour, minute);
        await rig.Scheduler.TickAsync(Cancel);
    }

    private static async Task DisconnectAsync(BoardTestRig rig)
    {
        rig.Simulator.RejectConnections = true;
        rig.Simulator.DropConnections();
        await BoardTestRig.WaitUntilAsync(() => !rig.Client.State.IsConnected);
    }

    private static async Task ReconnectAsync(BoardTestRig rig)
    {
        rig.Simulator.RejectConnections = false;
        await BoardTestRig.WaitUntilAsync(() => rig.Client.State.IsConnected);
    }

    private static async Task<int> AddScheduleAsync(BoardTestRig rig, int relayId, int onHour, int onMinute, int offHour, int offMinute)
    {
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        var schedule = new Schedule
        {
            RelayId = relayId,
            Days = ScheduleDays.All,
            OnTime = new TimeOnly(onHour, onMinute),
            OffTime = new TimeOnly(offHour, offMinute),
            IsEnabled = true,
            CreatedBy = Jane,
        };
        db.Schedules.Add(schedule);
        await db.SaveChangesAsync(Cancel);
        return schedule.Id;
    }

    private static async Task<RelayRuntime> RuntimeAsync(BoardTestRig rig, int relayId)
    {
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        return await db.RelayRuntimes.SingleAsync(r => r.RelayId == relayId, Cancel);
    }

    private static async Task<DateTime?> LastTickAsync(BoardTestRig rig)
    {
        await using var db = await rig.Database.Factory.CreateDbContextAsync(Cancel);
        return (await db.SchedulerStates.SingleAsync(Cancel)).LastTickUtc;
    }
}
