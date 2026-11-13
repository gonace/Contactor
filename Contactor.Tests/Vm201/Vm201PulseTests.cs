using Contactor.Boards;
using Contactor.Boards.Vm201;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Vm201;

/// <summary>
/// CMD_PULSE: the lengths the VM201 can send and their bytes, its trace text, and the client against the simulator,
/// whose pulse ends on its own clock.
/// </summary>
public sealed class Vm201PulseTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1, 1, 's')]
    [InlineData(2, 2, 's')]
    [InlineData(60, 60, 's')]
    [InlineData(99, 99, 's')]
    [InlineData(120, 2, 'm')]
    [InlineData(5940, 99, 'm')]
    [InlineData(7200, 2, 'h')]
    [InlineData(99 * 3600, 99, 'h')]
    public void Should_SendALengthExactly_InTheSmallestUnitThatCan(int seconds, int time, char unit)
    {
        Assert.True(Vm201Pulse.TryEncode(TimeSpan.FromSeconds(seconds), out var sentTime, out var sentUnit));
        Assert.Equal((byte)time, sentTime);
        Assert.Equal((byte)unit, sentUnit);
        Assert.Equal(TimeSpan.FromSeconds(seconds), Vm201Pulse.Decode(sentTime, sentUnit));
        Assert.True(Vm201Pulse.Ability.CanDo(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-60)]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(5941)]
    [InlineData(6000)]
    [InlineData(100 * 3600)]
    [InlineData(int.MaxValue)]
    public void Should_RefuseALengthItCantSendExactly(int seconds)
    {
        Assert.False(Vm201Pulse.TryEncode(TimeSpan.FromSeconds(seconds), out _, out _));
    }

    [Fact]
    public void Should_RefusePartsOfASecond_AndTheExtremes()
    {
        Assert.False(Vm201Pulse.TryEncode(TimeSpan.FromMilliseconds(1500), out _, out _));
        Assert.False(Vm201Pulse.TryEncode(TimeSpan.FromMilliseconds(500), out _, out _));
        Assert.False(Vm201Pulse.TryEncode(TimeSpan.Zero, out _, out _));
        Assert.False(Vm201Pulse.TryEncode(TimeSpan.MaxValue, out _, out _));
        Assert.False(Vm201Pulse.TryEncode(TimeSpan.MinValue, out _, out _));
    }

    [Fact]
    public void Should_HaveAnAbilityThatAllowsMoreThanItCanSend()
    {
        // CanDo is the closest a PulseAbility comes: 100 s is in it, but the board can't send it.
        Assert.Equal(new PulseAbility(TimeSpan.FromSeconds(1), TimeSpan.FromHours(99), TimeSpan.FromSeconds(1)), Vm201Pulse.Ability);
        Assert.True(Vm201Pulse.Ability.CanDo(TimeSpan.FromSeconds(100)));
        Assert.False(Vm201Pulse.TryEncode(TimeSpan.FromSeconds(100), out _, out _));
    }

    [Theory]
    [InlineData(0, 's')]
    [InlineData(100, 's')]
    [InlineData(255, 'h')]
    [InlineData(5, 'x')]
    [InlineData(5, 'S')]
    public void Should_DecodeNothing_ForATimeOrUnitTheBoardDoesntTake(int time, char unit)
    {
        Assert.Null(Vm201Pulse.Decode((byte)time, (byte)unit));
    }

    [Fact]
    public void Should_EncodeThePulsePacket()
    {
        Assert.True(Vm201Pulse.TryEncode(TimeSpan.FromSeconds(2), out var time, out var unit));

        var packet = Vm201PacketCodec.Encode(Vm201Command.Pulse, [Vm201Channel.Mask(2), time, unit]);

        Assert.Equal(new byte[] { 0x02, 0x08, 0x50, 0x02, 0x02, 0x73, 0x2F, 0x03 }, packet);
        Assert.Equal(packet.Length, Vm201Command.ExpectedPacketLength(Vm201Command.Pulse));
    }

    [Theory]
    [InlineData(0x02, 2, 's', "Pulse relay 2 for 2 s")]
    [InlineData(0x05, 5, 'm', "Pulse relay 1 and 3 for 5 min")]
    [InlineData(0xFF, 3, 'h', "Pulse all for 3 h")]
    [InlineData(0x01, 0, 's', "Pulse relay 1 for 0 s")]
    [InlineData(0x01, 7, 'x', "Pulse relay 1 for 7 x")]
    public void Should_DescribeAPulse(byte mask, int time, char unit, string expected)
    {
        var packet = Vm201PacketCodec.Encode(Vm201Command.Pulse, [mask, (byte)time, (byte)unit]);

        var (text, hex, isError) = Vm201TraceText.Describe(new Vm201TraceEntry(DateTimeOffset.UnixEpoch, Vm201TraceKind.Sent, packet, null));

        Assert.Equal(expected, text);
        Assert.Equal(string.Join(' ', packet.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture))), hex);
        Assert.False(isError);
    }

    [Fact]
    public async Task Should_SendThePulse_AndTheBoardTurnsTheRelaysOffByItself()
    {
        var time = new ManualTimeProvider();
        await using var simulator = new Vm201Simulator { TimeProvider = time };
        simulator.Start();
        await using var run = await ClientRun.StartAsync(simulator);

        await run.Client.PulseAsync(0x06, TimeSpan.FromSeconds(2), Cancel);

        await Wait.UntilAsync(() => run.Client.State.Outputs == 0x06 && time.WaitsOnly(TimeSpan.FromSeconds(2)));
        Assert.Equal(ChannelMask.Of([2, 3]), simulator.Pulsing);
        var sent = Assert.Single(simulator.ReceivedCommands, p => p.Command != Vm201Command.StatusRequest);
        Assert.Equal(Vm201Command.Pulse, sent.Command);
        Assert.Equal(new byte[] { 0x06, 2, (byte)'s' }, sent.Data.ToArray());

        // Not a moment early: the board's own clock ends it, and the app sends nothing more.
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0x06, simulator.Outputs);
        time.Advance(TimeSpan.FromSeconds(1));

        await Wait.UntilAsync(() => run.Client.State.Outputs == 0);
        Assert.Equal(ChannelMask.None, simulator.Pulsing);
        Assert.Equal(0, time.PendingTimers);
        Assert.Single(simulator.ReceivedCommands, p => p.Command != Vm201Command.StatusRequest);
    }

    [Fact]
    public async Task Should_EndThePulse_WhenTheRelayIsSwitchedOffDuringIt()
    {
        var time = new ManualTimeProvider();
        await using var simulator = new Vm201Simulator { TimeProvider = time };
        simulator.Start();
        await using var run = await ClientRun.StartAsync(simulator);
        await run.Client.PulseAsync(0x01, TimeSpan.FromMinutes(5), Cancel);
        await Wait.UntilAsync(() => time.Waits(TimeSpan.FromMinutes(5)));

        await run.Client.TurnOffAsync(0x01, Cancel);
        await Wait.UntilAsync(() => simulator.Pulsing == ChannelMask.None);
        await run.Client.TurnOnAsync(0x01, Cancel);
        await Wait.UntilAsync(() => run.Client.State.Outputs == 0x01);
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(0x01, simulator.Outputs);
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public async Task Should_StartThePulseAgain_WhenTheRelayIsPulsedDuringIt()
    {
        var time = new ManualTimeProvider();
        await using var simulator = new Vm201Simulator { TimeProvider = time };
        simulator.Start();
        await using var run = await ClientRun.StartAsync(simulator);
        await run.Client.PulseAsync(0x03, TimeSpan.FromSeconds(10), Cancel);
        await Wait.UntilAsync(() => time.Waits(TimeSpan.FromSeconds(10)));
        time.Advance(TimeSpan.FromSeconds(5));

        await run.Client.PulseAsync(0x01, TimeSpan.FromSeconds(10), Cancel);
        await Wait.UntilAsync(() => time.Waits(TimeSpan.FromSeconds(10)));
        time.Advance(TimeSpan.FromSeconds(5));

        // Relay 2 ends with its first pulse; relay 1 runs on with its second.
        await Wait.UntilAsync(() => run.Client.State.Outputs == 0x01);
        time.Advance(TimeSpan.FromSeconds(5));
        await Wait.UntilAsync(() => run.Client.State.Outputs == 0);
    }

    [Fact]
    public async Task Should_IgnoreAPulseWithATimeTheBoardDoesntTake()
    {
        var time = new ManualTimeProvider();
        await using var simulator = new Vm201Simulator { TimeProvider = time };
        simulator.Start();
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync("127.0.0.1", simulator.Port, Cancel);
        var stream = tcp.GetStream();

        await stream.WriteAsync(Vm201PacketCodec.Encode(Vm201Command.Pulse, [0x01, 0, (byte)'s']), Cancel);
        await stream.WriteAsync(Vm201PacketCodec.Encode(Vm201Command.Pulse, [0x01, 100, (byte)'s']), Cancel);
        await stream.WriteAsync(Vm201PacketCodec.Encode(Vm201Command.StatusRequest), Cancel);

        await Wait.UntilAsync(() => simulator.ReceivedCommands.Any(p => p.Command == Vm201Command.StatusRequest));
        Assert.Equal(0, simulator.Outputs);
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public async Task Should_StopItsPulses_WhenDisposed()
    {
        var time = new ManualTimeProvider();
        var simulator = new Vm201Simulator { TimeProvider = time };
        simulator.Start();
        await using (var run = await ClientRun.StartAsync(simulator))
        {
            await run.Client.PulseAsync(0x01, TimeSpan.FromSeconds(3), Cancel);
            await Wait.UntilAsync(() => time.Waits(TimeSpan.FromSeconds(3)));
        }

        await simulator.DisposeAsync();

        Assert.Equal(0, time.PendingTimers);
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(0x01, simulator.Outputs);
    }

    [Fact]
    public async Task Should_RefuseALengthBeforeAnythingIsSent()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        await using var run = await ClientRun.StartAsync(simulator);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.PulseAsync(0x01, TimeSpan.FromSeconds(100), Cancel));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.PulseAsync(0x01, TimeSpan.Zero, Cancel));
        await run.Client.RequestStatusAsync(Cancel);

        await Wait.UntilAsync(() => simulator.ReceivedCommands.Any(p => p.Command == Vm201Command.StatusRequest));
        Assert.DoesNotContain(simulator.ReceivedCommands, p => p.Command == Vm201Command.Pulse);
    }

    [Fact]
    public async Task Should_SendNothing_WithoutTheBoard()
    {
        var client = new Vm201Client(Options.Create(new Vm201Options { Host = "127.0.0.1", Port = 1 }), TimeProvider.System, NullLogger<Vm201Client>.Instance);

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => client.PulseAsync(0x01, TimeSpan.FromSeconds(2), Cancel));
    }

    // The client on the real clock (its keep-alive and timeouts), the simulator on the test's.
    private sealed class ClientRun : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _task;

        private ClientRun(Vm201Client client, CancellationTokenSource cts, Task task)
        {
            Client = client;
            _cts = cts;
            _task = task;
        }

        public Vm201Client Client { get; }

        public static async Task<ClientRun> StartAsync(Vm201Simulator simulator)
        {
            var options = new Vm201Options { Host = "127.0.0.1", Port = simulator.Port };
            var client = new Vm201Client(Options.Create(options), TimeProvider.System, NullLogger<Vm201Client>.Instance);
            var cts = new CancellationTokenSource();
            var run = new ClientRun(client, cts, client.RunAsync(cts.Token));
            await Wait.UntilAsync(() => client.State.IsConnected);
            return run;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await _task.WaitAsync(TimeSpan.FromSeconds(10));
            _cts.Dispose();
        }
    }
}
