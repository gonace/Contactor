using System.Net;
using System.Net.Sockets;
using Contactor.Boards;
using Contactor.Boards.Denkovi;
using Contactor.Boards.Devantech;
using Contactor.Boards.Shelly;
using Contactor.Boards.Velleman;
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
        Assert.Equal("Power back here. Every relay comes back off (not yet checked on a real VM201). Its timers stay.", board.Traffic[0].Text);
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
