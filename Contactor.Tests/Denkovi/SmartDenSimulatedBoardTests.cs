using System.Buffers.Binary;
using System.Net.Sockets;
using Contactor.Boards;
using Contactor.Boards.Denkovi;
using Contactor.Tests.Web;

namespace Contactor.Tests.Denkovi;

/// <summary>The simulated smartDEN over loopback, with raw Modbus TCP frames, as the real board answers them.</summary>
public sealed class SmartDenSimulatedBoardTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("00 01 00 00 00 06 00 01 00 00 00 11", "00 01 00 00 00 03 00 81 02")]
    [InlineData("00 01 00 00 00 06 00 01 00 0F 00 02", "00 01 00 00 00 03 00 81 02")]
    [InlineData("00 01 00 00 00 06 00 01 00 00 00 00", "00 01 00 00 00 03 00 81 03")]
    [InlineData("00 02 00 00 00 06 00 05 00 10 FF 00", "00 02 00 00 00 03 00 85 02")]
    [InlineData("00 02 00 00 00 06 00 05 00 00 12 34", "00 02 00 00 00 03 00 85 03")]
    [InlineData("00 03 00 00 00 06 00 06 00 10 00 1E", "00 03 00 00 00 03 00 86 02")]
    [InlineData("00 03 00 00 00 06 00 06 00 00 00 00", "00 03 00 00 00 03 00 86 03")]
    [InlineData("00 03 00 00 00 06 00 06 60 00 00 1E", "00 03 00 00 00 03 00 86 02")]
    [InlineData("00 04 00 00 00 06 00 03 6A 00 00 01", "00 04 00 00 00 03 00 83 02")]
    [InlineData("00 05 00 00 00 06 00 02 00 00 00 08", "00 05 00 00 00 03 00 82 01")]
    [InlineData("00 06 00 00 00 08 00 0F 00 0F 00 02 01 03", "00 06 00 00 00 03 00 8F 02")]
    public async Task Should_AnswerAnException_ForWhatTheBoardDoesntHave(string request, string answer)
    {
        await using var simulator = Started();

        Assert.Equal(answer, await AskAsync(simulator, request));
        Assert.Equal(0UL, simulator.Relays);
    }

    [Theory]
    [InlineData("00 01 00 00 00 02 00 01", "00 01 00 00 00 03 00 81 03")]
    [InlineData("00 01 00 00 00 08 00 01 00 00 00 10 00 00", "00 01 00 00 00 03 00 81 03")]
    [InlineData("00 02 00 00 00 05 00 05 00 00 FF", "00 02 00 00 00 03 00 85 03")]
    [InlineData("00 03 00 00 00 03 00 06 00", "00 03 00 00 00 03 00 86 03")]
    [InlineData("00 06 00 00 00 06 00 0F 00 00 00 01", "00 06 00 00 00 03 00 8F 03")]
    [InlineData("00 06 00 00 00 08 00 0F 00 00 00 10 01 FF", "00 06 00 00 00 03 00 8F 03")]
    [InlineData("00 06 00 00 00 0A 00 0F 00 00 00 10 02 FF FF 00", "00 06 00 00 00 03 00 8F 03")]
    public async Task Should_RefuseARequestOfTheWrongLength_AndServeTheNext(string request, string answer)
    {
        await using var simulator = Started();
        using var tcp = await ConnectAsync(simulator);
        var stream = tcp.GetStream();

        Assert.Equal(answer, await AskAsync(stream, request));

        // The same connection still answers.
        Assert.Equal("00 07 00 00 00 05 00 01 02 00 00", await AskAsync(stream, "00 07 00 00 00 06 00 01 00 00 00 10"));
    }

    [Theory]
    [InlineData(3, "00 01 00 00 00 04 00 01 01 05")]
    [InlineData(8, "00 01 00 00 00 04 00 01 01 05")]
    [InlineData(9, "00 01 00 00 00 05 00 01 02 05 01")]
    [InlineData(16, "00 01 00 00 00 05 00 01 02 05 81")]
    public async Task Should_AnswerAByteForEachEightRelaysAskedFor(ushort count, string answer)
    {
        await using var simulator = Started();
        await simulator.SwitchRelayAsync(1, true);
        await simulator.SwitchRelayAsync(3, true);
        await simulator.SwitchRelayAsync(9, true);
        await simulator.SwitchRelayAsync(16, true);

        Assert.Equal(answer, await AskAsync(simulator, SmartDenModbus.Hex(SmartDenModbus.ReadCoils(1, 0, count).Frame)));
    }

    [Fact]
    public async Task Should_AnswerWithTheRequestsTransactionAndUnit()
    {
        await using var simulator = Started();

        Assert.Equal("AB CD 00 00 00 05 07 01 02 00 00", await AskAsync(simulator, "AB CD 00 00 00 06 07 01 00 00 00 10"));
        Assert.Equal("12 34 00 00 00 06 FF 05 00 0F FF 00", await AskAsync(simulator, "12 34 00 00 00 06 FF 05 00 0F FF 00"));
        Assert.Equal(1UL << 15, simulator.Relays);
    }

    [Fact]
    public async Task Should_AnswerNothing_ForAnotherProtocol()
    {
        await using var simulator = Started();
        using var tcp = await ConnectAsync(simulator);
        var stream = tcp.GetStream();

        await stream.WriteAsync(SmartDenModbusTests.Bytes("00 01 00 01 00 06 00 01 00 00 00 10"), Cancel);

        // Only the next one is answered.
        Assert.Equal("00 02 00 00 00 05 00 01 02 00 00", await AskAsync(stream, "00 02 00 00 00 06 00 01 00 00 00 10"));
        Assert.Equal(1, simulator.UnansweredCount);
    }

    [Theory]
    [InlineData("00 01 00 00 00 00 00")]
    [InlineData("00 01 00 00 00 01 00")]
    [InlineData("00 01 00 00 FF FF 00")]
    [InlineData("00 01 00 00 00 FF 00")]
    public async Task Should_CloseTheConnection_ForALengthNoFrameHas_AndTakeTheNext(string header)
    {
        await using var simulator = Started();
        using (var tcp = await ConnectAsync(simulator))
        {
            var stream = tcp.GetStream();
            await stream.WriteAsync(SmartDenModbusTests.Bytes(header), Cancel);

            Assert.Equal(0, await stream.ReadAsync(new byte[1], Cancel));
        }

        await Wait.UntilAsync(() => simulator.ConnectionCount == 0);
        Assert.Equal("00 02 00 00 00 05 00 01 02 00 00", await AskAsync(simulator, "00 02 00 00 00 06 00 01 00 00 00 10"));
    }

    [Fact]
    public async Task Should_KeepServing_AfterAClientGoesAwayInTheMiddleOfAFrame()
    {
        await using var simulator = Started();
        using (var tcp = await ConnectAsync(simulator))
        {
            // Wait until the board took it. Else it can take this one and the next one together, and this one, with its
            // half frame not yet read, still looks open, so the board refuses the next one.
            await Wait.UntilAsync(() => simulator.ConnectionCount == 1);
            await tcp.GetStream().WriteAsync(SmartDenModbusTests.Bytes("00 01 00 00 00 06 00 05 00"), Cancel);
        }

        await Wait.UntilAsync(() => simulator.ConnectionCount == 0);
        Assert.Equal("00 02 00 00 00 06 00 05 00 00 FF 00", await AskAsync(simulator, "00 02 00 00 00 06 00 05 00 00 FF 00"));
        Assert.Equal(1UL, simulator.Relays);
    }

    [Fact]
    public async Task Should_RefuseASecondConnection_WhileOneIsOpen()
    {
        await using var simulator = Started();
        using var first = await ConnectAsync(simulator);
        await Wait.UntilAsync(() => simulator.ConnectionCount == 1);

        using (var second = await ConnectAsync(simulator))
        {
            Assert.Equal(0, await second.GetStream().ReadAsync(new byte[1], Cancel));
        }

        Assert.Equal(1, simulator.ConnectionCount);
        Assert.Equal("00 01 00 00 00 05 00 01 02 00 00", await AskAsync(first.GetStream(), "00 01 00 00 00 06 00 01 00 00 00 10"));

        // Once the first is closed, the next one is taken, also before the board saw the close by itself.
        first.Dispose();
        Assert.Equal("00 02 00 00 00 05 00 01 02 00 00", await AskAsync(simulator, "00 02 00 00 00 06 00 01 00 00 00 10"));
    }

    [Fact]
    public async Task Should_LetANewConnectionTakeOver_WhenSetTo()
    {
        await using var simulator = new SmartDenSimulator { NewConnectionTakesOver = true };
        simulator.Start();
        using var first = await ConnectAsync(simulator);
        await Wait.UntilAsync(() => simulator.ConnectionCount == 1);

        using var second = await ConnectAsync(simulator);

        Assert.Equal(0, await first.GetStream().ReadAsync(new byte[1], Cancel));
        await Wait.UntilAsync(() => simulator.State.Clients.Count == 1);
        Assert.Equal(1, simulator.ConnectionCount);
        Assert.Equal("00 01 00 00 00 05 00 01 02 00 00", await AskAsync(second.GetStream(), "00 01 00 00 00 06 00 01 00 00 00 10"));
        Assert.Equal(1, simulator.ConnectionCount);
    }

    [Fact]
    public async Task Should_PulseARelay_AndTurnItOffAfterTheTime_OnItsOwnClock()
    {
        var time = new ManualTimeProvider();
        await using var simulator = Started(time);

        Assert.Equal("00 05 00 00 00 06 00 06 00 09 00 1E", await AskAsync(simulator, "00 05 00 00 00 06 00 06 00 09 00 1E"));
        Assert.Equal(1UL << 9, simulator.Relays);
        Assert.Equal(ChannelMask.Of(10), simulator.Pulsing);
        time.Advance(TimeSpan.FromMilliseconds(2999));
        Assert.Equal(1UL << 9, simulator.Relays);
        time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(0UL, simulator.Relays);
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public async Task Should_EndAPulse_WhenTheRelayIsSwitched_AndStartItAgain_WhenPulsedAgain()
    {
        var time = new ManualTimeProvider();
        await using var simulator = Started(time);
        var pulse = SmartDenModbus.Hex(SmartDenModbus.Pulse(1, 1, TimeSpan.FromSeconds(1)).Frame);

        await AskAsync(simulator, pulse);
        time.Advance(TimeSpan.FromMilliseconds(500));
        await AskAsync(simulator, pulse);
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(1UL, simulator.Relays);

        await AskAsync(simulator, SmartDenModbus.Hex(SmartDenModbus.WriteSingleCoil(2, 1, on: true).Frame));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1UL, simulator.Relays);
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public async Task Should_WriteSeveralRelays_AndFlipOne()
    {
        await using var simulator = Started();

        // Table 10.16: relays 1 and 3 on, relay 2 off; then table 10.17: relay 1 off, 2 to 16 on.
        Assert.Equal("00 03 00 00 00 06 00 0F 00 00 00 03", await AskAsync(simulator, "00 03 00 00 00 08 00 0F 00 00 00 03 01 05"));
        Assert.Equal(0b101UL, simulator.Relays);
        Assert.Equal("00 03 00 00 00 06 00 0F 00 00 00 10", await AskAsync(simulator, "00 03 00 00 00 09 00 0F 00 00 00 10 02 FE FF"));
        Assert.Equal(0xFFFEUL, simulator.Relays);

        await AskAsync(simulator, "00 04 00 00 00 06 00 05 00 00 FF 02");
        Assert.Equal(0xFFFFUL, simulator.Relays);
    }

    [Fact]
    public async Task Should_RefuseTheRelaysItIsToldToFail()
    {
        await using var simulator = Started();
        simulator.FailRelays = 0b10;

        Assert.Equal("00 01 00 00 00 03 00 85 04", await AskAsync(simulator, "00 01 00 00 00 06 00 05 00 01 FF 00"));
        Assert.Equal("00 01 00 00 00 03 00 86 04", await AskAsync(simulator, "00 01 00 00 00 06 00 06 00 01 00 01"));
        Assert.Equal("00 01 00 00 00 06 00 05 00 00 FF 00", await AskAsync(simulator, "00 01 00 00 00 06 00 05 00 00 FF 00"));
        Assert.Equal(1UL, simulator.Relays);
    }

    [Fact]
    public async Task Should_StopItsPulses_WhenDisposed()
    {
        var time = new ManualTimeProvider();
        var simulator = Started(time);
        await AskAsync(simulator, "00 05 00 00 00 06 00 06 00 00 00 07");

        await simulator.DisposeAsync();
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(1UL, simulator.Relays);
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public async Task Should_ShowWhoIsConnected_AndDropThem()
    {
        await using var simulator = Started();
        using var tcp = await ConnectAsync(simulator);
        await Wait.UntilAsync(() => simulator.State.Clients.Count == 1);

        simulator.DropConnections();

        Assert.Equal(0, simulator.ConnectionCount);
        Assert.Empty(simulator.State.Clients);
        Assert.Equal(0, await tcp.GetStream().ReadAsync(new byte[1], Cancel));
    }

    [Fact]
    public async Task Should_RefuseInputsAndRelays_ItDoesntHave()
    {
        await using var simulator = new SmartDenSimulator();

        Assert.False(simulator.HasLogin);
        Assert.Same(SmartDenModels.Ip16RMt, simulator.Model);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => simulator.SetInputAsync(1, true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => simulator.SwitchRelayAsync(17, true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => simulator.SwitchRelayAsync(0, true));
    }

    private static SmartDenSimulator Started(TimeProvider? time = null)
    {
        var simulator = new SmartDenSimulator { TimeProvider = time ?? TimeProvider.System };
        simulator.Start();
        return simulator;
    }

    private static async Task<TcpClient> ConnectAsync(SmartDenSimulator simulator)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", simulator.Port, Cancel);
        return tcp;
    }

    private static async Task<string> AskAsync(SmartDenSimulator simulator, string request)
    {
        using var tcp = await ConnectAsync(simulator);
        return await AskAsync(tcp.GetStream(), request);
    }

    // Sends the request and reads one answer by its header's length.
    private static async Task<string> AskAsync(NetworkStream stream, string request)
    {
        await stream.WriteAsync(SmartDenModbusTests.Bytes(request), Cancel);
        var head = new byte[SmartDenApi.HeaderBytes];
        await stream.ReadExactlyAsync(head, Cancel);
        var length = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4));
        Assert.InRange(length, 2, SmartDenApi.MaxLength);
        var frame = new byte[SmartDenApi.HeaderBytes - 1 + length];
        head.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(SmartDenApi.HeaderBytes), Cancel);
        return SmartDenModbus.Hex(frame);
    }
}
