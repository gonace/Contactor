using System.Net.Sockets;
using Contactor.Boards;
using Contactor.Boards.Waveshare;
using Contactor.Tests.Vm208;

namespace Contactor.Tests.Waveshare;

/// <summary>The simulated board over loopback, with raw Modbus frames, as the real board answers them.</summary>
public sealed class WaveshareSimulatedBoardTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("01 01 00 00 00 09 FC 0C", "01 81 02 C1 91")]
    [InlineData("01 01 00 00 00 00 3C 0A", "01 81 03 00 51")]
    [InlineData("01 02 00 00 00 08 79 CC", "01 82 01 81 60")]
    [InlineData("01 05 00 08 FF 00 0D F8", "01 85 02 C3 51")]
    [InlineData("01 05 00 00 12 34 C0 BD", "01 85 03 02 91")]
    [InlineData("01 03 10 00 00 08 40 CC", "01 83 02 C0 F1")]
    [InlineData("01 07 00 00 00 00 B4 0A", "01 87 01 82 30")]
    public async Task Should_AnswerAnException_ForWhatTheBoardDoesntHave(string request, string answer)
    {
        // The 8-relay model without inputs.
        await using var simulator = new WaveshareSimulator(WaveshareModels.Relay8);
        simulator.Start();

        Assert.Equal(answer, await AskAsync(simulator, request));
    }

    [Fact]
    public async Task Should_AnswerNothing_ForABadCrcOrAnotherUnit()
    {
        await using var simulator = new WaveshareSimulator(WaveshareModels.Relay8);
        simulator.Start();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", simulator.Port, Cancel);
        var stream = tcp.GetStream();

        await stream.WriteAsync(WaveshareModbusTests.Bytes("01 01 00 00 00 08 3D CD"), Cancel);
        await stream.WriteAsync(WaveshareModbus.ReadCoils(2, 0, 8).Frame, Cancel);
        await stream.WriteAsync(WaveshareModbus.ReadCoils(1, 0, 8).Frame, Cancel);
        var answer = new byte[6];
        await stream.ReadExactlyAsync(answer, Cancel);

        // Only the last one is answered.
        Assert.Equal("01 01 01 00 51 88", WaveshareModbus.Hex(answer));
        Assert.Equal(2, simulator.UnansweredCount);
    }

    [Fact]
    public async Task Should_FlashARelayOn_AndTurnItOffAfterTheTime_OnItsOwnClock()
    {
        var time = new Web.ManualTimeProvider();
        await using var simulator = new WaveshareSimulator(WaveshareModels.Relay8) { TimeProvider = time };
        simulator.Start();

        Assert.Equal("01 05 02 00 00 07 8D B0", await AskAsync(simulator, "01 05 02 00 00 07 8D B0"));
        Assert.Equal(0b1UL, simulator.Relays);
        Assert.Equal(ChannelMask.Of(1), simulator.Pulsing);
        time.Advance(TimeSpan.FromMilliseconds(699));
        Assert.Equal(0b1UL, simulator.Relays);
        time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(0UL, simulator.Relays);
        Assert.Equal(0, time.PendingTimers);
    }

    [Theory]
    [InlineData("01 05 02 08 00 07 0C 72", "01 85 02 C3 51")]
    [InlineData("01 05 02 00 00 00 CC 72", "01 85 03 02 91")]
    [InlineData("01 05 02 00 80 00 AD B2", "01 85 03 02 91")]
    public async Task Should_RefuseAFlashOn_ItCantDo(string request, string answer)
    {
        var time = new Web.ManualTimeProvider();
        await using var simulator = new WaveshareSimulator(WaveshareModels.Relay8) { TimeProvider = time };
        simulator.Start();

        Assert.Equal(answer, await AskAsync(simulator, request));
        Assert.Equal(0UL, simulator.Relays);
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public async Task Should_EndAFlash_WhenTheRelayIsSwitchedForGood_AndLeaveALinkedRelay()
    {
        var time = new Web.ManualTimeProvider();
        await using var simulator = new WaveshareSimulator(WaveshareModels.Relay8B) { TimeProvider = time };
        simulator.Start();
        await simulator.SetRelayModeAsync(2, WaveshareApi.ModeLinkage);

        await AskAsync(simulator, WaveshareModbus.Hex(WaveshareModbus.FlashOn(1, 1, TimeSpan.FromSeconds(1)).Frame));
        await AskAsync(simulator, WaveshareModbus.Hex(WaveshareModbus.FlashOn(1, 2, TimeSpan.FromSeconds(1)).Frame));
        Assert.Equal(ChannelMask.Of(1), simulator.Pulsing);
        await AskAsync(simulator, "01 05 00 00 FF 00 8C 3A");
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0b01UL, simulator.Relays);
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public async Task Should_StopItsFlashes_WhenDisposed()
    {
        var time = new Web.ManualTimeProvider();
        var simulator = new WaveshareSimulator(WaveshareModels.Relay8) { TimeProvider = time };
        simulator.Start();
        await AskAsync(simulator, "01 05 02 00 00 07 8D B0");

        await simulator.DisposeAsync();
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0b1UL, simulator.Relays);
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public async Task Should_SwitchAllRelays_AtAddressFF()
    {
        await using var simulator = new WaveshareSimulator(WaveshareModels.Relay8);
        simulator.Start();

        Assert.Equal("01 05 00 FF FF 00 BC 0A", await AskAsync(simulator, "01 05 00 FF FF 00 BC 0A"));
        Assert.Equal(0xFFUL, simulator.Relays);
    }

    [Fact]
    public async Task Should_FollowTheInputs_InEachMode()
    {
        await using var simulator = new WaveshareSimulator(WaveshareModels.Relay8B);
        simulator.Start();
        await simulator.SetRelayModeAsync(1, WaveshareApi.ModeLinkage);
        await simulator.SetRelayModeAsync(2, WaveshareApi.ModeToggle);
        await simulator.SetRelayModeAsync(3, WaveshareApi.ModeEdge);

        await simulator.SetInputAsync(1, true);
        await simulator.SetInputAsync(2, true);
        await simulator.SetInputAsync(3, true);
        await simulator.SetInputAsync(4, true);
        Assert.Equal(0b0111UL, simulator.Relays);

        await simulator.SetInputAsync(1, false);
        await simulator.SetInputAsync(2, false);
        await simulator.SetInputAsync(3, false);
        Assert.Equal(0b0010UL, simulator.Relays);

        // A relay in linkage mode takes no command.
        Assert.Equal("01 05 00 00 FF 00 8C 3A", await AskAsync(simulator, "01 05 00 00 FF 00 8C 3A"));
        Assert.Equal(0b0010UL, simulator.Relays);
    }

    [Fact]
    public async Task Should_ReadAndWriteTheModeRegisters()
    {
        await using var simulator = new WaveshareSimulator(WaveshareModels.Relay8C);
        simulator.Start();

        Assert.Equal("01 06 10 01 00 03 9C CB", await AskAsync(simulator, "01 06 10 01 00 03 9C CB"));
        Assert.Equal(WaveshareApi.ModeEdge, simulator.RelayModes[1]);
        Assert.Equal("01 03 04 00 00 00 03 BA 32", await AskAsync(simulator, "01 03 10 00 00 02 C0 CB"));
        Assert.Equal("01 86 03 02 61", await AskAsync(simulator, "01 06 10 01 00 04 DD 09"));
        Assert.Equal("01 86 02 C3 A1", await AskAsync(simulator, "01 06 10 08 00 01 CD 08"));

        var write = WaveshareModbus.WriteMultipleRegisters(1, 0x1000, new ushort[8]);
        Assert.Equal(WaveshareModbus.Hex(WaveshareModbus.WithCrc([1, 0x10, 0x10, 0, 0, 8])), await AskAsync(simulator, WaveshareModbus.Hex(write.Frame)));
        Assert.All(simulator.RelayModes, m => Assert.Equal(0, m));
    }

    [Fact]
    public async Task Should_ShowWhoIsConnected_AndDropThem()
    {
        await using var simulator = new WaveshareSimulator(WaveshareModels.Relay16);
        simulator.Start();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", simulator.Port, Cancel);
        await Wait.UntilAsync(() => simulator.State.Clients.Count == 1);

        simulator.DropConnections();

        await Wait.UntilAsync(() => simulator.State.Clients.Count == 0);
        Assert.Equal(0, await tcp.GetStream().ReadAsync(new byte[1], Cancel));
    }

    [Fact]
    public async Task Should_RefuseInputsAndModes_ItDoesntHave()
    {
        await using var simulator = new WaveshareSimulator(WaveshareModels.Relay30);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => simulator.SetInputAsync(1, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => simulator.SetRelayModeAsync(1, WaveshareApi.ModeLinkage));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => simulator.SwitchRelayAsync(31, true));
        Assert.Throws<ArgumentException>(() => new WaveshareSimulator(Contactor.Boards.Vm201.Vm201Model.Model));
    }

    private static async Task<string> AskAsync(WaveshareSimulator simulator, string request)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", simulator.Port, Cancel);
        var stream = tcp.GetStream();
        await stream.WriteAsync(WaveshareModbusTests.Bytes(request), Cancel);

        // Read what comes: an exception answer is 5 bytes, the others at most 9 here.
        var answer = new byte[WaveshareApi.MaxFrameBytes];
        var length = 0;
        while (length < 5 || !WaveshareModbus.HasValidCrc(answer.AsSpan(0, length)))
        {
            var read = await stream.ReadAsync(answer.AsMemory(length), Cancel);
            Assert.NotEqual(0, read);
            length += read;
        }

        return WaveshareModbus.Hex(answer.AsSpan(0, length));
    }
}
