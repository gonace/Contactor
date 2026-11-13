using System.Net.Sockets;
using Contactor.Boards;
using Contactor.Boards.Devantech;
using Contactor.Tests.Vm208;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Tests.Devantech;

/// <summary>The simulated board over loopback, with raw bytes, as the real board answers them.</summary>
public sealed class DevantechSimulatedBoardTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_AnswerTheRecordedEth008Session()
    {
        await using var simulator = Started(DevantechModels.Eth008);
        await simulator.SwitchRelayAsync(1, true);
        await simulator.SwitchRelayAsync(3, true);
        await simulator.SwitchRelayAsync(4, true);
        using var board = await Board.ConnectAsync(simulator);

        Assert.Equal("13 02 1C", await board.AskAsync("10", 3));
        Assert.Equal("FF", await board.AskAsync("7A", 1));
        Assert.Equal("0D", await board.AskAsync("24", 1));
        Assert.Equal("00", await board.AskAsync("20 08 00", 1));
        Assert.Equal("8D", await board.AskAsync("24", 1));
        Assert.Equal("00", await board.AskAsync("21 08 00", 1));
        Assert.Equal("00", await board.AskAsync("23 0D", 1));
        Assert.Equal("0D", await board.AskAsync("24", 1));
        Assert.Equal("01", await board.AskAsync("20 00 00", 1));
        Assert.Equal("01", await board.AskAsync("20 09 00", 1));
        Assert.Equal(0b1101UL, simulator.Relays);
    }

    [Fact]
    public async Task Should_SwitchARelayBack_AfterItsPulseTime_OnItsOwnClock()
    {
        var time = new ManualTimeProvider();
        await using var simulator = Started(DevantechModels.Eth008, time: time);
        await simulator.SwitchRelayAsync(2, true);
        using var board = await Board.ConnectAsync(simulator);

        Assert.Equal("00", await board.AskAsync("20 01 05", 1));
        Assert.Equal("00", await board.AskAsync("21 02 FF", 1));
        Assert.Equal("01", await board.AskAsync("24", 1));
        Assert.Equal(ChannelMask.Of([1, 2]), simulator.Pulsing);
        time.Advance(TimeSpan.FromMilliseconds(499));
        Assert.Equal("01", await board.AskAsync("24", 1));
        time.Advance(TimeSpan.FromMilliseconds(1));

        // 0x20 turns it off again; 0x21 on again, after 25.5 s.
        Assert.Equal("00", await board.AskAsync("24", 1));
        time.Advance(TimeSpan.FromMilliseconds(25_000));
        Assert.Equal("02", await board.AskAsync("24", 1));
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public async Task Should_EndAPulse_WhenTheRelayIsSwitchedForGood()
    {
        var time = new ManualTimeProvider();
        await using var simulator = Started(DevantechModels.Eth008, time: time);
        using var board = await Board.ConnectAsync(simulator);

        Assert.Equal("00", await board.AskAsync("20 01 0A", 1));
        Assert.Equal("00", await board.AskAsync("20 02 0A", 1));
        Assert.Equal("00", await board.AskAsync("20 01 00", 1));
        Assert.Equal("00", await board.AskAsync("23 03", 1));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("03", await board.AskAsync("24", 1));
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public async Task Should_StopItsPulses_WhenDisposed()
    {
        var time = new ManualTimeProvider();
        var simulator = Started(DevantechModels.Eth008, time: time);
        using (var board = await Board.ConnectAsync(simulator))
        {
            Assert.Equal("00", await board.AskAsync("20 01 0A", 1));
        }

        await simulator.DisposeAsync();
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(0b1UL, simulator.Relays);
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public async Task Should_AnswerNothing_ForACommandTheModelDoesntHave()
    {
        await using var simulator = Started(DevantechModels.Eth008);
        using var board = await Board.ConnectAsync(simulator);

        // One packet each: the board reads a packet as the commands in it, and an unknown one ends the packet.
        await board.SendAsync("25");
        await Wait.UntilAsync(() => simulator.UnansweredCount == 1);
        await board.SendAsync("3A 44 4F 41");
        await Wait.UntilAsync(() => simulator.UnansweredCount == 2);

        // The next command is answered as usual.
        Assert.Equal("00", await board.AskAsync("24", 1));
        Assert.Equal(["25", "3A …", "24"], simulator.ReceivedRequests);
    }

    [Fact]
    public async Task Should_NeverShowAPassword_InAPacketItCantRead()
    {
        await using var simulator = Started(DevantechModels.Eth008, "apple", new ManualTimeProvider());
        List<BoardTraceEntry> traces = [];
        simulator.Traced += (_, entry) =>
        {
            lock (traces)
            {
                traces.Add(entry);
            }
        };
        using var board = await Board.ConnectAsync(simulator);

        await board.SendAsync("25 79 61 70 70 6C 65");
        await Wait.UntilAsync(() => simulator.UnansweredCount == 1);

        Assert.Equal(["25 …"], simulator.ReceivedRequests);
        lock (traces)
        {
            Assert.DoesNotContain(traces, t => (t.Bytes ?? "").Contains("61", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Should_LayOutTheEth8020_AsItsTableSays()
    {
        await using var simulator = Started(DevantechModels.Eth8020);
        await simulator.SwitchRelayAsync(1, true);
        await simulator.SwitchRelayAsync(3, true);
        await simulator.SwitchRelayAsync(20, true);
        await simulator.SetInputAsync(1, true);
        await simulator.SetInputAsync(8, true);
        using var board = await Board.ConnectAsync(simulator);

        Assert.Equal("15 02 1C", await board.AskAsync("10", 3));
        Assert.Equal("05 00 08", await board.AskAsync("24", 3));
        Assert.Equal("00 00 00 81", await board.AskAsync("25", 4));
        Assert.Equal("00", await board.AskAsync("23 00 00 00", 1));
        Assert.Equal(0UL, simulator.Relays);
    }

    [Fact]
    public async Task Should_LayOutTheEth484_AsItsTableSays()
    {
        await using var simulator = Started(DevantechModels.Eth484);
        await simulator.SwitchRelayAsync(4, true);
        await simulator.SetInputAsync(2, true);
        using var board = await Board.ConnectAsync(simulator);

        Assert.Equal("14 02 1C", await board.AskAsync("10", 3));
        Assert.Equal("08 00", await board.AskAsync("24", 2));
        Assert.Equal("00 02", await board.AskAsync("25", 2));
        Assert.Equal("01", await board.AskAsync("20 05 00", 1));
    }

    [Fact]
    public async Task Should_StayLocked_UntilThePasswordComes()
    {
        var time = new ManualTimeProvider();
        await using var simulator = Started(DevantechModels.Eth002, "apple", time);
        using var board = await Board.ConnectAsync(simulator);

        Assert.Equal("00", await board.AskAsync("7A", 1));
        Assert.Equal("01", await board.AskAsync("20 01 00", 1));
        Assert.Equal("00", await board.AskAsync("24", 1));
        Assert.Equal("02", await board.AskAsync("79 70 65 61 72", 1));
        Assert.Equal("01", await board.AskAsync("79 61 70 70 6C 65", 1));
        Assert.Equal("1E", await board.AskAsync("7A", 1));
        Assert.Equal("00", await board.AskAsync("20 01 00", 1));
        Assert.Equal(0b01UL, simulator.Relays);
        Assert.Contains(DevantechTraceText.HiddenPassword, simulator.ReceivedRequests);
        Assert.DoesNotContain(simulator.ReceivedRequests, r => r.Contains("61 70", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_LockAgain_After30sWithoutACommand()
    {
        var time = new ManualTimeProvider();
        await using var simulator = Started(DevantechModels.Eth008, "apple", time);
        using var board = await Board.ConnectAsync(simulator);
        Assert.Equal("01", await board.AskAsync("79 61 70 70 6C 65", 1));

        // A command within the 30 s keeps it unlocked for another 30 s, the lock check too, as the documents say.
        time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal("1E", await board.AskAsync("7A", 1));
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal("00", await board.AskAsync("20 01 00", 1));
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("00", await board.AskAsync("7A", 1));
        Assert.Equal("01", await board.AskAsync("21 01 00", 1));
        Assert.Equal(0b01UL, simulator.Relays);
    }

    [Fact]
    public async Task Should_UnlockOnlyTheConnectionThatSentThePassword()
    {
        await using var simulator = Started(DevantechModels.Eth008, "apple", new ManualTimeProvider());
        using var first = await Board.ConnectAsync(simulator);
        using var second = await Board.ConnectAsync(simulator);

        Assert.Equal("01", await first.AskAsync("79 61 70 70 6C 65", 1));

        Assert.Equal("00", await second.AskAsync("7A", 1));
        Assert.Equal("01", await second.AskAsync("20 01 00", 1));
        Assert.Equal("00", await first.AskAsync("7B", 1));
        Assert.Equal("00", await first.AskAsync("7A", 1));
    }

    [Fact]
    public async Task Should_RefuseTheRelaysItIsToldTo()
    {
        await using var simulator = Started(DevantechModels.Eth008);
        simulator.FailRelays = 0b10;
        using var board = await Board.ConnectAsync(simulator);

        Assert.Equal("01", await board.AskAsync("20 02 00", 1));
        Assert.Equal("00", await board.AskAsync("20 01 00", 1));
        Assert.Equal(0b01UL, simulator.Relays);
    }

    [Fact]
    public async Task Should_ShowWhoIsConnected_AndDropThem()
    {
        await using var simulator = Started(DevantechModels.Eth008);
        using var board = await Board.ConnectAsync(simulator);
        await Wait.UntilAsync(() => simulator.State.Clients.Count == 1);

        simulator.DropConnections();

        await Wait.UntilAsync(() => simulator.State.Clients.Count == 0);
        Assert.Equal(0, await board.Stream.ReadAsync(new byte[1], Cancel));
    }

    [Fact]
    public async Task Should_RefuseWhatItDoesntHave()
    {
        await using var simulator = new DevantechSimulator(DevantechModels.Eth008);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => simulator.SetInputAsync(1, true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => simulator.SwitchRelayAsync(9, true));
        Assert.Throws<ArgumentException>(() => new DevantechSimulator(Contactor.Boards.Vm201.Vm201Model.Model));
        Assert.Throws<ArgumentException>(() => new DevantechSimulator(DevantechModels.Eth008, "two words", NullLogger.Instance));
        Assert.False(simulator.HasLogin);
    }

    private static DevantechSimulator Started(BoardModel model, string? password = null, TimeProvider? time = null)
    {
        var simulator = new DevantechSimulator(model, password, NullLogger.Instance) { TimeProvider = time ?? TimeProvider.System };
        simulator.Start();
        return simulator;
    }

    private sealed class Board : IDisposable
    {
        private readonly TcpClient _tcp;

        private Board(TcpClient tcp)
        {
            _tcp = tcp;
            Stream = tcp.GetStream();
        }

        public NetworkStream Stream { get; }

        public static async Task<Board> ConnectAsync(DevantechSimulator simulator)
        {
            var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync("127.0.0.1", simulator.Port, Cancel);
            return new Board(tcp);
        }

        public async Task SendAsync(string command)
        {
            await Stream.WriteAsync(DevantechProtocolTests.Bytes(command), Cancel);
        }

        public async Task<string> AskAsync(string command, int answerLength)
        {
            await SendAsync(command);
            return DevantechProtocol.Hex(await DevantechProtocol.ReadAnswerAsync(Stream, answerLength, Cancel));
        }

        public void Dispose()
        {
            _tcp.Dispose();
        }
    }
}
