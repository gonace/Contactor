using Contactor.Boards;
using Contactor.Boards.Denkovi;
using Contactor.Boards.Shelly;
using Contactor.Boards.Velleman;
using Contactor.Simulator;
using Contactor.Tests.Web;

namespace Contactor.Tests.Simulator;

/// <summary>"Run now" for a simulated board's own rules: the board switches as the rule would at its time.</summary>
public sealed class SimulatedRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Should_RunAVm201Timer_OnlyWhileItIsOn()
    {
        await using var vm201 = new Vm201Simulator();
        Assert.Empty(vm201.RunnableRules);
        Assert.Null(await vm201.RunRuleAsync("timer-3-on"));

        await vm201.SetTimerAsync(3, true);

        Assert.Equal(["timer-3-on", "timer-3-off"], vm201.RunnableRules.Select(r => r.Id));
        Assert.Equal("Relay 3's timer: on", (await vm201.RunRuleAsync("timer-3-on"))!.Name);
        Assert.Equal(ChannelMask.Of(3), vm201.State.Relays);
        await vm201.RunRuleAsync("timer-3-off");
        Assert.True(vm201.State.Relays.IsEmpty);
    }

    [Fact]
    public async Task Should_RunAVm208Schedule_ByItsAlarmsThatAreOn()
    {
        await using var vm208 = new Vm208Simulator();
        Assert.Empty(vm208.RunnableRules);

        // Only an "on" alarm on Monday is on: the schedule can switch relay 2 on, not off.
        var alarms = Vm208Simulator.FactorySchedule.ToArray();
        alarms[Vm208Alarm.IndexOf(1, turnsOn: true)] = alarms[Vm208Alarm.IndexOf(1, turnsOn: true)] with { Hour = 6, Minute = 30, Enabled = true };
        await vm208.SetAlarmsAsync(2, alarms);

        Assert.Equal(["alarm-2-on"], vm208.RunnableRules.Select(r => r.Id));
        Assert.Null(await vm208.RunRuleAsync("alarm-2-off"));
        await vm208.RunRuleAsync("alarm-2-on");
        Assert.Equal(ChannelMask.Of(2), vm208.State.Relays);
    }

    [Fact]
    public async Task Should_RunAShellySchedule_AndAutoOn()
    {
        await using var shelly = new ShellySimulator(ShellyModels.FourRelays);
        var on = shelly.AddSchedule("""[{"method":"Switch.Set","params":{"id":0,"on":true}},{"method":"Switch.Set","params":{"id":2,"on":true}}]""");
        var off = shelly.AddSchedule("""[{"method":"Switch.Set","params":{"id":0,"on":false}}]""", enable: false);
        shelly.AddSchedule("""[{"method":"Shelly.Reboot"}]""");
        shelly.SetAutoOn(4, true);

        // The schedule that is off, and the one without a Switch.Set, can't run.
        Assert.Equal([$"schedule-{on}", "auto-on-4"], shelly.RunnableRules.Select(r => r.Id));
        Assert.Null(await shelly.RunRuleAsync($"schedule-{off}"));

        await shelly.RunRuleAsync($"schedule-{on}");
        Assert.Equal(ChannelMask.Of([1, 3]), shelly.State.Relays);
        await shelly.RunRuleAsync("auto-on-4");
        Assert.Equal(ChannelMask.Of([1, 3, 4]), shelly.State.Relays);
    }

    [Fact]
    public async Task Should_RunASmartDenRow_ItsRelaysTogether_OnlyWhileItIsOn()
    {
        await using var smartDen = new SmartDenSimulator();
        await smartDen.SetRowAsync(new SmartDenSimulatedRow(4, Enabled: true, Relays: 0b101, TurnOn: true, Hour: 6, Minute: 30, WeekDays: 0x3E));
        await smartDen.SetRowAsync(new SmartDenSimulatedRow(5, Enabled: false, Relays: 0b010, TurnOn: true, Hour: 7, Minute: 0, WeekDays: 0x7F));
        await smartDen.SetRowAsync(new SmartDenSimulatedRow(6, Enabled: true, Relays: 0b001, TurnOn: false, Hour: 22, Minute: 0, WeekDays: 0x7F));

        Assert.Equal(["row-4", "row-6"], smartDen.RunnableRules.Select(r => r.Id));
        Assert.Equal("Row 4: Relays 1 and 3 on at 06:30, Mon, Tue, Wed, Thu, Fri", smartDen.RunnableRules[0].Name);
        Assert.Null(await smartDen.RunRuleAsync("row-5"));

        await smartDen.RunRuleAsync("row-4");
        Assert.Equal(ChannelMask.Of([1, 3]), smartDen.State.Relays);
        await smartDen.RunRuleAsync("row-6");
        Assert.Equal(ChannelMask.Of(3), smartDen.State.Relays);
    }

    [Fact]
    public async Task Should_RunNothing_WhileThePowerIsCut()
    {
        var time = new ManualTimeProvider();
        await using var board = new RunningBoard(1, new Vm201Simulator(), null, fromSettings: false, time);
        await board.SetTimerAsync(1, true);
        _ = board.PowerCutAsync(TimeSpan.FromSeconds(5));

        await board.RunRuleAsync("timer-1-on");

        Assert.True(board.Board.State.Relays.IsEmpty);
    }

    [Fact]
    public async Task Should_SayWhatRan_InTheTraffic()
    {
        await using var board = new RunningBoard(1, new Vm201Simulator(), null, fromSettings: false, new FixedTimeProvider(Now));
        await board.SetTimerAsync(5, true);

        await board.RunRuleAsync("timer-5-on");

        Assert.Equal("Run now here: Relay 5's timer: on (relay 5 on)", board.Traffic[0].Text);
        Assert.Equal(ChannelMask.Of(5), board.Board.State.Relays);
    }
}
