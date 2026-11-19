using System.Net;
using System.Net.Sockets;
using Contactor.Boards;
using Contactor.Boards.Denkovi;
using Contactor.Boards.Devantech;
using Contactor.Boards.Shelly;
using Contactor.Boards.Velleman;
using Contactor.Boards.Waveshare;
using Contactor.Simulator;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Tests.Simulator;

/// <summary>A simulated board's power cut: what it does while off, and how its relays come back.</summary>
public sealed class SimulatedPowerTests
{
    public static TheoryData<string> Models => [.. SimulatorCatalogue.All.Select(m => m.Model.Id)];

    [Theory]
    [MemberData(nameof(Models))]
    public async Task Should_TurnEveryRelayOff_WhileThePowerIsCut_AndBringThemBackOffByDefault(string type)
    {
        await using var board = Create(type);
        var power = Assert.IsAssignableFrom<ISimulatedBoardPower>(board);
        await board.SwitchRelayAsync(1, true);

        await power.CutPowerAsync();

        Assert.True(power.IsPoweredOff);
        Assert.True(board.State.Relays.IsEmpty);

        await power.RestorePowerAsync();

        Assert.False(power.IsPoweredOff);
        Assert.True(board.State.Relays.IsEmpty);
    }

    [Theory]
    [MemberData(nameof(Models))]
    public async Task Should_TakeNoConnection_WhileThePowerIsCut(string type)
    {
        await using var board = Create(type);
        if (board is not ISimulatedBoardRejectConnections)
        {
            // The DAEnetIP2 speaks UDP: DaenetIp2SimulatedBoardTests shows it answers nothing while off.
            return;
        }

        board.Start();
        await ((ISimulatedBoardPower)board).CutPowerAsync();

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, board.Port, TestContext.Current.CancellationToken);
        var read = await ReadAsync(tcp);

        Assert.Equal(0, read);
    }

    [Theory]
    [MemberData(nameof(Models))]
    public async Task Should_ChangeNothing_WhileThePowerIsCut(string type)
    {
        await using var board = Create(type);
        var power = (ISimulatedBoardPower)board;
        await power.CutPowerAsync();

        await board.SwitchRelayAsync(1, true);
        Assert.True(board.State.Relays.IsEmpty);

        await power.RestorePowerAsync();
        Assert.True(board.State.Relays.IsEmpty);
    }

    [Theory]
    [MemberData(nameof(Models))]
    public async Task Should_DropTheOpenConnection_AndTakeANewOneOnceThePowerIsBack(string type)
    {
        await using var board = Create(type);
        if (board is not ISimulatedBoardRejectConnections)
        {
            return;
        }

        board.Start();
        var power = (ISimulatedBoardPower)board;
        using (var before = new TcpClient())
        {
            await before.ConnectAsync(IPAddress.Loopback, board.Port, TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => board.State.Clients.Count == 1);

            await power.CutPowerAsync();

            Assert.Equal(0, await ReadUntilClosedAsync(before));
        }

        await power.RestorePowerAsync();
        await WaitUntilAsync(() => board.State.Clients.Count == 0);
        using var after = new TcpClient();
        await after.ConnectAsync(IPAddress.Loopback, board.Port, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => board.State.Clients.Count == 1);
    }

    [Fact]
    public async Task Should_KeepTheBoardsOwnRules_ThroughAPowerCut()
    {
        await using var vm201 = new Vm201Simulator();
        await vm201.SetTimerAsync(3, true);
        await using var vm208 = new Vm208Simulator();
        var alarms = Vm208Simulator.FactorySchedule.ToArray();
        alarms[Vm208Alarm.IndexOf(2, turnsOn: true)] = alarms[Vm208Alarm.IndexOf(2, turnsOn: true)] with { Hour = 7, Enabled = true };
        await vm208.SetAlarmsAsync(1, alarms);
        await using var waveshare = new WaveshareSimulator(WaveshareModels.Relay8B);
        await ((ISimulatedBoardRelayModes)waveshare).SetModeAsync(4, 1);

        foreach (var power in new ISimulatedBoardPower[] { vm201, vm208, waveshare })
        {
            await power.CutPowerAsync();
            await power.RestorePowerAsync();
        }

        Assert.Equal(ChannelMask.Of(3), vm201.Timers);
        Assert.Equal(alarms, vm208.Alarms(1));
        Assert.Equal(1, ((ISimulatedBoardRelayModes)waveshare).Modes[3]);
    }

    [Fact]
    public async Task Should_BringTheRelaysBack_WithSaveOutputsOrLatchedOutputs()
    {
        await using var smartDen = new SmartDenSimulator { SaveOutputs = true };
        await using var daenet = new DaenetIp2Simulator(DaenetIp2Models.Relays8, null, NullLogger.Instance) { SaveOutputs = true };
        await using var devantech = new DevantechSimulator(DevantechModels.Eth008) { LatchedOutputs = true };
        ISimulatedBoard[] boards = [smartDen, daenet, devantech];

        foreach (var board in boards)
        {
            await board.SwitchRelayAsync(2, true);
            var power = (ISimulatedBoardPower)board;
            await power.CutPowerAsync();
            Assert.True(board.State.Relays.IsEmpty);

            await power.RestorePowerAsync();

            Assert.Equal(ChannelMask.Of(2), board.State.Relays);
        }
    }

    [Fact]
    public async Task Should_BringEachShellyRelayBack_AsItsInitialStateSays()
    {
        await using var shelly = new ShellySimulator(ShellyModels.FourRelays);
        shelly.SetInitialState(1, "on");
        shelly.SetInitialState(2, "restore_last");
        shelly.SetInitialState(3, "restore_last");
        shelly.SetInitialState(4, "match_input");
        await shelly.SwitchRelayAsync(2, true);
        await shelly.SwitchRelayAsync(4, true);

        await shelly.CutPowerAsync();
        await shelly.RestorePowerAsync();

        // Relay 3 was off before the cut; relay 4 follows an input the simulator doesn't have.
        Assert.Equal(ChannelMask.Of([1, 2]), shelly.State.Relays);
    }

    [Fact]
    public async Task Should_GiveThePowerBack_AfterTheCutOnTheSimulatorsClock()
    {
        var time = new ManualTimeProvider();
        await using var board = new RunningBoard(1, new Vm201Simulator(), null, fromSettings: false, time);
        await board.SwitchRelayAsync(3, true);

        var cut = board.PowerCutAsync(TimeSpan.FromSeconds(30));

        Assert.True(board.Power!.IsPoweredOff);
        Assert.True(board.Board.State.Relays.IsEmpty);
        Assert.Equal("Power cut for 30 s here", board.Traffic[0].Text);

        // Without power, the board's own buttons switch nothing.
        await board.SwitchRelayAsync(2, true);
        Assert.True(board.Board.State.Relays.IsEmpty);

        // A second cut while it is off changes nothing.
        await board.PowerCutAsync(TimeSpan.FromSeconds(5));
        Assert.True(time.Waits(TimeSpan.FromSeconds(30)));

        time.Advance(TimeSpan.FromSeconds(30));
        await cut;

        Assert.False(board.Power.IsPoweredOff);
        Assert.Equal("Power back here. Every relay comes back off, and its timers stay (neither yet checked on a real VM201).", board.Traffic[0].Text);
    }

    [Fact]
    public async Task Should_LeaveTheBoardWithoutPower_WhenItIsRemovedDuringTheCut()
    {
        var time = new ManualTimeProvider();
        var board = new RunningBoard(1, new Vm201Simulator(), null, fromSettings: false, time);
        var cut = board.RestartAsync();
        Assert.True(board.Power!.IsPoweredOff);

        await board.DisposeAsync();
        await cut;

        Assert.True(board.Power.IsPoweredOff);
    }

    private static ISimulatedBoard Create(string type)
    {
        return SimulatorCatalogue.Find(type)!.Create(null, null, NullLogger.Instance, TimeProvider.System);
    }

    // Until the board's state shows it; the test's token stops a board that never does.
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        while (!condition())
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    // Reads until the board closes the connection, whatever it sent before: 0 then.
    private static async Task<int> ReadUntilClosedAsync(TcpClient tcp)
    {
        var buffer = new byte[256];
        try
        {
            while (await tcp.GetStream().ReadAsync(buffer, TestContext.Current.CancellationToken) > 0)
            {
            }

            return 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    // Reads until the board closes the connection: 0 then. Ends on its own; the test's token only stops a broken board.
    private static async Task<int> ReadAsync(TcpClient tcp)
    {
        var buffer = new byte[256];
        try
        {
            return await tcp.GetStream().ReadAsync(buffer, TestContext.Current.CancellationToken);
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
