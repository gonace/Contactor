using System.Buffers.Binary;
using System.Net.Sockets;
using Contactor.Boards;
using Contactor.Boards.Advantech;
using Contactor.Tests.Web;

namespace Contactor.Tests.Advantech;

/// <summary>The simulated ADAM modules over loopback, with raw Modbus TCP frames, as the manual says the module answers them.</summary>
public sealed class AdamSimulatedBoardTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // A read of the six relays, and its answer with all of them off.
    private const string ReadRelays = "00 07 00 00 00 06 01 01 00 10 00 06";
    private const string NoRelays = "00 07 00 00 00 04 01 01 01 00";

    [Theory]
    [InlineData("00 01 00 00 00 06 01 01 00 10 00 07", "00 01 00 00 00 03 01 81 02")]
    [InlineData("00 01 00 00 00 06 01 01 00 0F 00 02", "00 01 00 00 00 03 01 81 02")]
    [InlineData("00 01 00 00 00 06 01 01 00 17 00 08", "00 01 00 00 00 03 01 81 02")]
    [InlineData("00 01 00 00 00 06 01 01 00 10 00 00", "00 01 00 00 00 03 01 81 03")]
    [InlineData("00 01 00 00 00 06 01 02 00 00 00 07", "00 01 00 00 00 03 01 82 02")]
    [InlineData("00 01 00 00 00 06 01 02 00 10 00 01", "00 01 00 00 00 03 01 82 02")]
    [InlineData("00 02 00 00 00 06 01 05 00 16 FF 00", "00 02 00 00 00 03 01 85 02")]
    [InlineData("00 02 00 00 00 06 01 05 00 00 FF 00", "00 02 00 00 00 03 01 85 02")]
    [InlineData("00 02 00 00 00 06 01 05 00 10 12 34", "00 02 00 00 00 03 01 85 03")]
    [InlineData("00 02 00 00 00 06 01 05 00 10 FF FF", "00 02 00 00 00 03 01 85 03")]
    [InlineData("00 03 00 00 00 06 01 03 00 01 00 02", "00 03 00 00 00 03 01 83 02")]
    [InlineData("00 03 00 00 00 06 01 04 00 01 00 02", "00 03 00 00 00 03 01 84 02")]
    [InlineData("00 03 00 00 00 06 01 06 00 01 00 64", "00 03 00 00 00 03 01 86 02")]
    [InlineData("00 03 00 00 00 06 01 08 00 00 12 34", "00 03 00 00 00 03 01 88 01")]
    [InlineData("00 06 00 00 00 08 01 0F 00 15 00 02 01 03", "00 06 00 00 00 03 01 8F 02")]
    public async Task Should_AnswerAnException_ForWhatTheModuleDoesntHave(string request, string answer)
    {
        await using var simulator = Started();

        Assert.Equal(answer, await AskAsync(simulator, request));
        Assert.Equal(0UL, simulator.Relays);
    }

    [Theory]
    [InlineData("00 01 00 00 00 02 01 01", "00 01 00 00 00 03 01 81 03")]
    [InlineData("00 01 00 00 00 08 01 01 00 10 00 06 00 00", "00 01 00 00 00 03 01 81 03")]
    [InlineData("00 02 00 00 00 05 01 05 00 10 FF", "00 02 00 00 00 03 01 85 03")]
    [InlineData("00 06 00 00 00 06 01 0F 00 10 00 01", "00 06 00 00 00 03 01 8F 03")]
    [InlineData("00 06 00 00 00 08 01 0F 00 10 00 10 01 FF", "00 06 00 00 00 03 01 8F 03")]
    [InlineData("00 06 00 00 00 0A 01 0F 00 10 00 06 02 FF FF 00", "00 06 00 00 00 03 01 8F 03")]
    public async Task Should_RefuseARequestOfTheWrongLength_AndServeTheNext(string request, string answer)
    {
        await using var simulator = Started();
        using var tcp = await ConnectAsync(simulator);
        var stream = tcp.GetStream();

        Assert.Equal(answer, await AskAsync(stream, request));

        // The same connection still answers.
        Assert.Equal(NoRelays, await AskAsync(stream, ReadRelays));
    }

    [Theory]
    [InlineData("00 01 00 00 00 06 01 01 00 10 00 06", "00 01 00 00 00 04 01 01 01 25")]
    [InlineData("00 01 00 00 00 06 01 01 00 12 00 04", "00 01 00 00 00 04 01 01 01 09")]
    [InlineData("00 01 00 00 00 06 01 02 00 00 00 06", "00 01 00 00 00 04 01 02 01 22")]
    [InlineData("00 01 00 00 00 06 01 02 00 01 00 01", "00 01 00 00 00 04 01 02 01 01")]
    public async Task Should_AnswerTheRelaysAndInputsAskedFor_BitZeroFirst(string request, string answer)
    {
        await using var simulator = Started();
        await simulator.SwitchRelayAsync(1, true);
        await simulator.SwitchRelayAsync(3, true);
        await simulator.SwitchRelayAsync(6, true);
        await simulator.SetInputAsync(2, true);
        await simulator.SetInputAsync(6, true);

        Assert.Equal(answer, await AskAsync(simulator, request));
    }

    [Fact]
    public async Task Should_AnswerWithTheRequestsTransactionAndUnit()
    {
        await using var simulator = Started();

        Assert.Equal("AB CD 00 00 00 04 07 01 01 00", await AskAsync(simulator, "AB CD 00 00 00 06 07 01 00 10 00 06"));
        Assert.Equal("12 34 00 00 00 06 FF 05 00 15 FF 00", await AskAsync(simulator, "12 34 00 00 00 06 FF 05 00 15 FF 00"));
        Assert.Equal(1UL << 5, simulator.Relays);
    }

    [Fact]
    public async Task Should_AnswerNothing_ForAnotherProtocol()
    {
        await using var simulator = Started();
        using var tcp = await ConnectAsync(simulator);
        var stream = tcp.GetStream();

        await stream.WriteAsync(AdamModbusTests.Bytes("00 01 00 01 00 06 01 01 00 10 00 06"), Cancel);

        // Only the next one is answered.
        Assert.Equal(NoRelays, await AskAsync(stream, ReadRelays));
        Assert.Equal(1, simulator.UnansweredCount);
    }

    [Theory]
    [InlineData("00 01 00 00 00 00 01")]
    [InlineData("00 01 00 00 00 01 01")]
    [InlineData("00 01 00 00 FF FF 01")]
    [InlineData("00 01 00 00 00 FF 01")]
    public async Task Should_CloseTheConnection_ForALengthNoFrameHas_AndTakeTheNext(string header)
    {
        await using var simulator = Started();
        using (var tcp = await ConnectAsync(simulator))
        {
            var stream = tcp.GetStream();
            await stream.WriteAsync(AdamModbusTests.Bytes(header), Cancel);

            Assert.Equal(0, await stream.ReadAsync(new byte[1], Cancel).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Cancel));
        }

        await Wait.UntilAsync(() => simulator.ConnectionCount == 0);
        Assert.Equal(NoRelays, await AskAsync(simulator, ReadRelays));
    }

    [Fact]
    public async Task Should_KeepServing_AfterAClientGoesAwayInTheMiddleOfAFrame()
    {
        await using var simulator = Started();
        using (var tcp = await ConnectAsync(simulator))
        {
            await tcp.GetStream().WriteAsync(AdamModbusTests.Bytes("00 01 00 00 00 06 01 05 00"), Cancel);
        }

        await Wait.UntilAsync(() => simulator.ConnectionCount == 0);
        Assert.Equal("00 02 00 00 00 06 01 05 00 10 FF 00", await AskAsync(simulator, "00 02 00 00 00 06 01 05 00 10 FF 00"));
        Assert.Equal(1UL, simulator.Relays);
    }

    [Fact]
    public async Task Should_TakeSeveralConnections_AtOnce()
    {
        // The manual names no limit, so the simulator sets none.
        await using var simulator = Started();
        using var first = await ConnectAsync(simulator);
        using var second = await ConnectAsync(simulator);
        await Wait.UntilAsync(() => simulator.ConnectionCount == 2 && simulator.State.Clients.Count == 2);

        Assert.Equal(NoRelays, await AskAsync(first.GetStream(), ReadRelays));
        Assert.Equal(NoRelays, await AskAsync(second.GetStream(), ReadRelays));
    }

    [Fact]
    public async Task Should_WriteSeveralRelays()
    {
        await using var simulator = Started();

        // Relays 1 and 3 on, relay 2 off; then relays 2 to 6 on and relay 1 off, as section 7.3.2's FC 15 maps the bits.
        Assert.Equal("00 03 00 00 00 06 01 0F 00 10 00 03", await AskAsync(simulator, "00 03 00 00 00 08 01 0F 00 10 00 03 01 05"));
        Assert.Equal(0b101UL, simulator.Relays);
        Assert.Equal("00 03 00 00 00 06 01 0F 00 10 00 06", await AskAsync(simulator, "00 03 00 00 00 08 01 0F 00 10 00 06 01 3E"));
        Assert.Equal(0x3EUL, simulator.Relays);
    }

    [Fact]
    public async Task Should_RefuseTheRelaysItIsToldToFail()
    {
        await using var simulator = Started();
        simulator.FailRelays = 0b10;

        Assert.Equal("00 01 00 00 00 03 01 85 04", await AskAsync(simulator, "00 01 00 00 00 06 01 05 00 11 FF 00"));
        Assert.Equal("00 01 00 00 00 03 01 8F 04", await AskAsync(simulator, "00 01 00 00 00 08 01 0F 00 10 00 02 01 03"));
        Assert.Equal("00 01 00 00 00 06 01 05 00 10 FF 00", await AskAsync(simulator, "00 01 00 00 00 06 01 05 00 10 FF 00"));
        Assert.Equal(1UL, simulator.Relays);
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
    public async Task Should_ShowItsRelaysAndInputs_AndRefuseOnesItDoesntHave()
    {
        await using var simulator = new AdamSimulator(AdamModels.Adam6066);
        await simulator.SwitchRelayAsync(6, true);
        await simulator.SetInputAsync(1, true);

        Assert.False(simulator.HasLogin);
        Assert.Same(AdamModels.Adam6066, simulator.Model);
        Assert.Equal(ChannelMask.Of(6), simulator.State.Relays);
        Assert.Equal(ChannelMask.Of(1), simulator.State.Inputs);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => simulator.SetInputAsync(7, true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => simulator.SetInputAsync(0, true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => simulator.SwitchRelayAsync(7, true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => simulator.SwitchRelayAsync(0, true));
        Assert.Throws<ArgumentException>(() => new AdamSimulator(Contactor.Boards.Velleman.Vm201Model.Model));
    }

    private static AdamSimulator Started()
    {
        var simulator = new AdamSimulator(AdamModels.Adam6060);
        simulator.Start();
        return simulator;
    }

    private static async Task<TcpClient> ConnectAsync(AdamSimulator simulator)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", simulator.Port, Cancel);
        return tcp;
    }

    private static async Task<string> AskAsync(AdamSimulator simulator, string request)
    {
        using var tcp = await ConnectAsync(simulator);
        return await AskAsync(tcp.GetStream(), request);
    }

    // Sends the request and reads one answer by its header's length.
    private static async Task<string> AskAsync(NetworkStream stream, string request)
    {
        await stream.WriteAsync(AdamModbusTests.Bytes(request), Cancel);
        var head = new byte[AdamApi.HeaderBytes];
        await stream.ReadExactlyAsync(head, Cancel);
        var length = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4));
        Assert.InRange(length, 2, AdamApi.MaxLength);
        var frame = new byte[AdamApi.HeaderBytes - 1 + length];
        head.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(AdamApi.HeaderBytes), Cancel);
        return AdamModbus.Hex(frame);
    }
}
