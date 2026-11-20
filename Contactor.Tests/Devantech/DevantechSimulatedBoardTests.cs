using System.Globalization;
using System.Net.Sockets;
using Contactor.Boards;
using Contactor.Boards.Devantech;
using Contactor.Boards.Velleman;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Tests.Devantech;

/// <summary>The simulated board over loopback, with raw bytes, as the real board answers them.</summary>
public sealed class DevantechSimulatedBoardTests
{
    // Far longer than the simulator takes to answer over loopback; only reached when an answer is short or missing.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

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
    public async Task Should_LayOutTheEth1610_AsItsManualSays()
    {
        var time = new ManualTimeProvider();
        await using var simulator = Started(DevantechModels.Eth1610, time: time);
        await simulator.SwitchRelayAsync(1, true);
        await simulator.SwitchRelayAsync(3, true);
        await simulator.SwitchRelayAsync(10, true);
        using var board = await Board.ConnectAsync(simulator);

        // Module id 52, relays 1–8 in the first byte of 0x24 and 9–10 in the second, 2 bytes to 0x25 (manual p. 17).
        Assert.Equal("34 02 1C", await board.AskAsync("10", 3));
        Assert.Equal("05 02", await board.AskAsync("24", 2));
        Assert.Equal("00 00", await board.AskAsync("25", 2));

        // The manual's example: output 2 on for 5 s (0x32 × 100 ms), then off by itself.
        Assert.Equal("00", await board.AskAsync("20 02 32", 1));
        Assert.Equal("07 02", await board.AskAsync("24", 2));
        time.Advance(TimeSpan.FromMilliseconds(4900));
        Assert.Equal(0b10_0000_0111UL, simulator.Relays);
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal("05 02", await board.AskAsync("24", 2));

        // 0x23 with 2 bytes sets all ten; there is no relay 11.
        Assert.Equal("00", await board.AskAsync("23 FF 03", 1));
        Assert.Equal("FF 03", await board.AskAsync("24", 2));
        Assert.Equal("01", await board.AskAsync("20 0B 00", 1));
        Assert.Equal(0b11_1111_1111UL, simulator.Relays);

        // The app doesn't use its inputs (their order isn't in the manual), so none can be set at the board.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => simulator.SetInputAsync(1, true));
    }

    [Fact]
    public async Task Should_AnswerNothingTo0x78_OnTheEth1610()
    {
        // The ETH1610's command table has no 0x78 (manual pp. 17–18); the ETH008-B answers it.
        await using var simulator = Started(DevantechModels.Eth1610);
        using var board = await Board.ConnectAsync(simulator);

        await board.SendAsync("78");
        await Wait.UntilAsync(() => simulator.UnansweredCount == 1);
        Assert.Equal("00 00", await board.AskAsync("24", 2));

        await using var eth008 = Started(DevantechModels.Eth008);
        using var other = await Board.ConnectAsync(eth008);
        Assert.Equal("78", await other.AskAsync("78", 1));
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

    // A power cut ends the pulses; with Latched Outputs a relay comes back as it was before its pulse.
    [Fact]
    public async Task Should_EndThePulses_ThroughAPowerCut_AndLatchOnlyThePermanentStates()
    {
        var time = new ManualTimeProvider();
        await using var simulator = Started(DevantechModels.Eth008, time: time);
        simulator.LatchedOutputs = true;
        using (var board = await Board.ConnectAsync(simulator))
        {
            Assert.Equal("00", await board.AskAsync("20 02 00", 1));
            Assert.Equal("00", await board.AskAsync("20 01 0A", 1));
            Assert.Equal("00", await board.AskAsync("21 02 0A", 1));
        }

        Assert.Equal(ChannelMask.Of([1, 2]), simulator.Pulsing);
        await simulator.CutPowerAsync();

        Assert.Equal(ChannelMask.None, simulator.Pulsing);
        Assert.Equal(0, time.PendingTimers);
        await simulator.RestorePowerAsync();

        // Relay 2 was on for good before its off-pulse; relay 1 only in its pulse.
        Assert.Equal(ChannelMask.Of(2), simulator.State.Relays);
    }

    // An off-pulse of a relay that is off is no permanent change: Latched Outputs brings it back off.
    [Fact]
    public async Task Should_LatchTheRelayOff_AfterAnOffPulseOfARelayThatWasOff()
    {
        var time = new ManualTimeProvider();
        await using var simulator = Started(DevantechModels.Eth008, time: time);
        simulator.LatchedOutputs = true;
        using (var board = await Board.ConnectAsync(simulator))
        {
            Assert.Equal("00", await board.AskAsync("21 02 0A", 1));
        }

        // The pulse's end turns it on, as 0x21 does; still no permanent change.
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ChannelMask.Of(2), simulator.State.Relays);

        await simulator.CutPowerAsync();
        await simulator.RestorePowerAsync();

        Assert.True(simulator.State.Relays.IsEmpty);
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
    public async Task Should_EndAPulse_WhenEveryRelayIsSet()
    {
        var time = new ManualTimeProvider();
        await using var simulator = Started(DevantechModels.Eth008, time: time);
        using var board = await Board.ConnectAsync(simulator);

        Assert.Equal("00", await board.AskAsync("20 01 0A", 1));
        Assert.Equal("00", await board.AskAsync("23 01", 1));
        Assert.Equal(ChannelMask.None, simulator.Pulsing);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("01", await board.AskAsync("24", 1));
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

    // A slow board answers after its delay on its own clock, not before.
    [Fact]
    public async Task Should_Answer_OnlyAfterItsAnswerDelay()
    {
        var time = new ManualTimeProvider();
        await using var simulator = Started(DevantechModels.Eth008, time: time);
        simulator.AnswerDelay = TimeSpan.FromSeconds(4);
        using var board = await Board.ConnectAsync(simulator);

        var answer = board.AskAsync("24", 1);
        await Wait.UntilAsync(() => time.Waits(TimeSpan.FromSeconds(4)));
        Assert.False(answer.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(4));

        Assert.Equal("00", await answer);
    }

    // The password changed on the board while it runs: the old one no longer unlocks, the new one does.
    [Fact]
    public async Task Should_TakeOnlyTheNewPassword_AfterItChanged()
    {
        var time = new ManualTimeProvider();
        await using var simulator = Started(DevantechModels.Eth002, "apple", time);
        simulator.ChangeLogin("", "pear");
        using var board = await Board.ConnectAsync(simulator);

        Assert.Equal("02", await board.AskAsync("79 61 70 70 6C 65", 1));
        Assert.Equal("01", await board.AskAsync("79 70 65 61 72", 1));
        Assert.Equal("1E", await board.AskAsync("7A", 1));
        Assert.Throws<ArgumentException>(() => simulator.ChangeLogin("", ""));
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
        Assert.Equal(0, await board.ReadAsync(new byte[1]));
    }

    [Fact]
    public async Task Should_RefuseWhatItDoesntHave()
    {
        await using var simulator = new DevantechSimulator(DevantechModels.Eth008);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => simulator.SetInputAsync(1, true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => simulator.SwitchRelayAsync(9, true));
        Assert.Throws<ArgumentException>(() => new DevantechSimulator(Vm201Model.Model));
        Assert.Throws<ArgumentException>(() => new DevantechSimulator(DevantechModels.Eth008, "two words", NullLogger.Instance));
        Assert.False(simulator.HasLogin);
    }

    [Fact]
    public async Task Should_AnswerOneAnalogInput_InTwoBytes_HighByteFirst()
    {
        // 0x32 and the channel; 2 bytes, high byte first, the value in the lower 10 bits (ETH8020-B p. 17, ETH484-B
        // p. 18, ETH1610 p. 18).
        await using var eth8020 = Started(DevantechModels.Eth8020);
        await eth8020.SetAnalogAsync(1, 512);
        await eth8020.SetAnalogAsync(8, 1023);
        using var board = await Board.ConnectAsync(eth8020);
        Assert.Equal("02 00", await board.AskAsync("32 01", 2));
        Assert.Equal("00 00", await board.AskAsync("32 02", 2));
        Assert.Equal("03 FF", await board.AskAsync("32 08", 2));

        await using var eth484 = Started(DevantechModels.Eth484);
        await eth484.SetAnalogAsync(4, 258);
        using var other = await Board.ConnectAsync(eth484);
        Assert.Equal("01 02", await other.AskAsync("32 04", 2));

        // The ETH1610 has 0x32 too.
        await using var eth1610 = Started(DevantechModels.Eth1610);
        await eth1610.SetAnalogAsync(16, 1);
        using var third = await Board.ConnectAsync(eth1610);
        Assert.Equal("00 01", await third.AskAsync("32 10", 2));
    }

    [Fact]
    public async Task Should_AnswerAllSixteenAnalogInputs_InThirtyTwoBytes_OnTheEth1610()
    {
        // 0x34: "all 16 inputs … high and then low byte for each with input 1 transmitted first. A total of 32 bytes"
        // (ETH1610 p. 18).
        await using var simulator = Started(DevantechModels.Eth1610);
        await simulator.SetAnalogAsync(1, 1023);
        await simulator.SetAnalogAsync(2, 256);
        await simulator.SetAnalogAsync(16, 5);
        using var board = await Board.ConnectAsync(simulator);

        var answer = await board.AskAsync("34", 32);

        Assert.Equal("03 FF 01 00" + string.Concat(Enumerable.Repeat(" 00 00", 13)) + " 00 05", answer);
        Assert.Equal([1023, 256, .. Enumerable.Repeat(0, 13), 5], simulator.State.Analog);
    }

    [Fact]
    public async Task Should_AnswerNothing_ForAnAnalogInputOrCommandTheModelHasNot_AndCarryOn()
    {
        await using var eth484 = Started(DevantechModels.Eth484);
        using var board = await Board.ConnectAsync(eth484);

        // No input 5 or 0, a 0x32 cut short, and no 0x34 on the ETH484-B: no answer, and the next command as usual.
        await board.SendAsync("32 05");
        await Wait.UntilAsync(() => eth484.UnansweredCount == 1);
        await board.SendAsync("32 00");
        await Wait.UntilAsync(() => eth484.UnansweredCount == 2);
        await board.SendAsync("32");
        await Wait.UntilAsync(() => eth484.UnansweredCount == 3);
        await board.SendAsync("34");
        await Wait.UntilAsync(() => eth484.UnansweredCount == 4);
        Assert.Equal("00 00", await board.AskAsync("32 01", 2));

        // No analog inputs on the ETH008-B.
        await using var eth008 = Started(DevantechModels.Eth008);
        using var other = await Board.ConnectAsync(eth008);
        await other.SendAsync("32 01");
        await Wait.UntilAsync(() => eth008.UnansweredCount == 1);
        Assert.Equal("00", await other.AskAsync("24", 1));
    }

    [Fact]
    public async Task Should_SetTheAnalogInputs_AndTellOnlyOfAChange()
    {
        await using var simulator = new DevantechSimulator(DevantechModels.Eth8020);
        var changes = 0;
        simulator.Changed += (_, _) => Interlocked.Increment(ref changes);
        Assert.Equal(new int[8], simulator.State.Analog);

        await simulator.SetAnalogAsync(8, 1023);
        await simulator.SetAnalogAsync(8, 1023);

        Assert.Equal(1, Volatile.Read(ref changes));
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 1023], simulator.State.Analog);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => simulator.SetAnalogAsync(0, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => simulator.SetAnalogAsync(9, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => simulator.SetAnalogAsync(1, 1024));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => simulator.SetAnalogAsync(1, -1));

        await using var eth008 = new DevantechSimulator(DevantechModels.Eth008);
        Assert.Empty(eth008.State.Analog);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => eth008.SetAnalogAsync(1, 0));
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

        private NetworkStream Stream { get; }

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
            return DevantechProtocol.Hex(await BoundedAsync(
                token => DevantechProtocol.ReadAnswerAsync(Stream, answerLength, token),
                string.Create(CultureInfo.InvariantCulture, $"{answerLength} bytes in answer to 0x{command.Replace(" ", " 0x", StringComparison.Ordinal)}")));
        }

        // A read of what the board sends next, e.g. 0 bytes once it closed the connection.
        public Task<int> ReadAsync(byte[] buffer)
        {
            return BoundedAsync(token => Stream.ReadAsync(buffer, token).AsTask(), "the board's next bytes");
        }

        // A short answer or none must fail the test, not hang the run until it is stopped.
        private static async Task<T> BoundedAsync<T>(Func<CancellationToken, Task<T>> read, string what)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
            timeout.CancelAfter(ReadTimeout);
            try
            {
                return await read(timeout.Token);
            }
            catch (OperationCanceledException) when (!Cancel.IsCancellationRequested)
            {
                throw new TimeoutException(string.Create(CultureInfo.InvariantCulture, $"The board did not send {what} within {ReadTimeout.TotalSeconds} s."));
            }
        }

        public void Dispose()
        {
            _tcp.Dispose();
        }
    }
}
