using System.Net;
using System.Net.Sockets;
using Contactor.Boards;
using Contactor.Boards.Waveshare;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Waveshare;

/// <summary>
/// The client against the simulator, and against a scripted board for answers the simulator never gives. Its clock is a
/// <see cref="ManualTimeProvider"/>: a poll, a wait or a timeout only comes when the test moves the clock.
/// </summary>
public sealed class WaveshareClientTests
{
    private const string ReadEightRelays = "01 01 00 00 00 08 3D CC";
    private const string ReadEightInputs = "01 02 00 00 00 08 79 CC";
    private const string ReadModes = "01 03 10 00 00 08 40 CC";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ReportTheRelaysAndInputs_OfAModelWithInputs()
    {
        await using var simulator = Started(WaveshareModels.Relay8B);
        await simulator.SwitchRelayAsync(2, true);
        await simulator.SetInputAsync(3, true);
        await using var run = ClientRun.Start(simulator.Port, WaveshareModels.Relay8B);

        // Connected, and the first answer reported: the client waits for the next poll.
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        Assert.Equal(new WaveshareStatus(0b0010, 0b0100), run.Client.State.Status);
        Assert.Equal(run.Time.GetUtcNow(), run.Client.State.LastContactUtc);
        Assert.Equal([ReadModes, ReadEightRelays, ReadEightInputs], simulator.ReceivedRequests);
        Assert.Contains(run.Changes, s => s.IsConnected && s.Relays == 0b0010 && s.Inputs == 0b0100);
    }

    [Fact]
    public async Task Should_AskOnlyForTheRelays_OnAModelWithoutInputs()
    {
        await using var simulator = Started(WaveshareModels.Relay8);
        await using var run = ClientRun.Start(simulator.Port, WaveshareModels.Relay8);

        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal([ReadEightRelays], simulator.ReceivedRequests);
        Assert.Equal(0UL, run.Client.State.Inputs);
    }

    [Fact]
    public async Task Should_SwitchAndReadAllThirtyRelays()
    {
        await using var simulator = Started(WaveshareModels.Relay30);
        await simulator.SwitchRelayAsync(30, true);
        await using var run = ClientRun.Start(simulator.Port, WaveshareModels.Relay30);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        Assert.True(run.Client.State.IsOn(30));

        await run.Client.TurnOnAsync(1UL << 28, Cancel);

        Assert.Equal((1UL << 28) | (1UL << 29), simulator.Relays);
        Assert.True(run.Client.State.IsOn(29));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.TurnOnAsync(1UL << 30, Cancel));
    }

    [Fact]
    public async Task Should_SwitchOneRelayPerRequest_AndReadTheStateAfter()
    {
        await using var simulator = Started(WaveshareModels.Relay8);
        await using var run = ClientRun.Start(simulator.Port, WaveshareModels.Relay8);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        await run.Client.TurnOnAsync(0b0101, Cancel);

        // Confirmed by the read in the command itself: no time had to pass for a poll.
        Assert.Equal(0b0101UL, simulator.Relays);
        Assert.Equal(0b0101UL, run.Client.State.Relays);
        Assert.Contains(run.Changes, s => s.Relays == 0b0101);

        await run.Client.TurnOffAsync(0b0001, Cancel);
        Assert.Equal(0b0100UL, run.Client.State.Relays);
        Assert.Equal(
            [
                ReadEightRelays,
                "01 05 00 00 FF 00 8C 3A",
                "01 05 00 02 FF 00 2D FA",
                ReadEightRelays,
                "01 05 00 00 00 00 CD CA",
                ReadEightRelays,
            ],
            simulator.ReceivedRequests);
        Assert.Contains(run.Traces, t => t.Text == "Turn on relay 3" && t.Direction == BoardTraceDirection.Sent && !t.IsRoutine);
        Assert.Contains(run.Traces, t => t.Text == "Relay 3 turned on" && t.Direction == BoardTraceDirection.Received);
    }

    [Fact]
    public async Task Should_PulseOneRelayPerRequest_AndTheBoardTurnsThemOffByItself()
    {
        var boardTime = new ManualTimeProvider();
        await using var simulator = new WaveshareSimulator(WaveshareModels.Relay8) { TimeProvider = boardTime };
        simulator.Start();
        await using var run = ClientRun.Start(simulator.Port, WaveshareModels.Relay8);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal(0b0101UL, await run.Client.PulseAsync(0b0101, TimeSpan.FromMilliseconds(500), Cancel));

        Assert.Equal(0b0101UL, run.Client.State.Relays);
        Assert.Equal([ReadEightRelays, "01 05 02 00 00 05 0C 71", "01 05 02 02 00 05 AD B1", ReadEightRelays], simulator.ReceivedRequests);
        Assert.Contains(run.Traces, t => t.Text == "Pulse relay 3 for 0.5 s" && t.Direction == BoardTraceDirection.Sent);
        Assert.Contains(run.Traces, t => t.Text == "Relay 3 turned on for 0.5 s" && t.Direction == BoardTraceDirection.Received);

        // The board's own clock ends it; the app only polls.
        boardTime.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(0UL, simulator.Relays);
        await run.PollAsync();
        Assert.Equal(0UL, run.Client.State.Relays);
        Assert.All(simulator.ReceivedRequests.Skip(4), r => Assert.Equal(ReadEightRelays, r));
    }

    [Fact]
    public async Task Should_PulseTheWholeMask_WhenTheBoardRefusesOneRelay()
    {
        var boardTime = new ManualTimeProvider();
        await using var simulator = new WaveshareSimulator(WaveshareModels.Relay8) { TimeProvider = boardTime, FailRelays = 0b0010 };
        simulator.Start();
        await using var run = ClientRun.Start(simulator.Port, WaveshareModels.Relay8);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal(0b0101UL, await run.Client.PulseAsync(0b0111, TimeSpan.FromSeconds(1), Cancel));

        Assert.Equal(0b0101UL, simulator.Relays);
        Assert.Equal(ChannelMask.Of([1, 3]), simulator.Pulsing);
        Assert.Contains(run.Traces, t => t.Text == "Refused: it failed (code 4)" && t.IsError);
        Assert.True(run.Client.State.IsConnected);

        simulator.FailRelays = 0b0111;
        var error = await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.PulseAsync(0b0011, TimeSpan.FromSeconds(1), Cancel));
        Assert.StartsWith("The Waveshare board didn't take the command: ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    [InlineData(150)]
    [InlineData(3_276_800)]
    public async Task Should_RefuseAPulseLength_BeforeAnythingIsSent(int milliseconds)
    {
        await using var simulator = Started(WaveshareModels.Relay8);
        await using var run = ClientRun.Start(simulator.Port, WaveshareModels.Relay8);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.PulseAsync(0b0001, TimeSpan.FromMilliseconds(milliseconds), Cancel));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.PulseAsync(1UL << 8, TimeSpan.FromSeconds(1), Cancel));

        Assert.Equal([ReadEightRelays], simulator.ReceivedRequests);
    }

    [Fact]
    public async Task Should_RefuseRelaysTheBoardHasNot_AndSendNothingForNone()
    {
        await using var simulator = Started(WaveshareModels.Relay8);
        await using var run = ClientRun.Start(simulator.Port, WaveshareModels.Relay8);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.TurnOnAsync(1UL << 8, Cancel));
        await run.Client.TurnOffAsync(0, Cancel);

        Assert.Equal([ReadEightRelays], simulator.ReceivedRequests);
    }

    [Fact]
    public async Task Should_SendTheWholeMask_WhenTheBoardRefusesOneRelay()
    {
        await using var simulator = Started(WaveshareModels.Relay8);
        simulator.FailRelays = 0b0010;
        await using var run = ClientRun.Start(simulator.Port, WaveshareModels.Relay8);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        // Only the relays it took.
        Assert.Equal(0b0101UL, await run.Client.TurnOnAsync(0b0111, Cancel));

        Assert.Equal(0b0101UL, simulator.Relays);
        Assert.Equal(0b0101UL, run.Client.State.Relays);
        Assert.Contains(run.Traces, t => t.Text == "Refused: it failed (code 4)" && t.IsError);
        Assert.Contains(run.Traces, t => t.Text.StartsWith("The board didn't take the command for relay 2: ", StringComparison.Ordinal) && t.IsError);
        Assert.True(run.Client.State.IsConnected);

        // OFF, as "All off" sends it: only the relays it took.
        Assert.Equal(0b0101UL, await run.Client.TurnOffAsync(0b0111, Cancel));
        Assert.Equal(0UL, simulator.Relays);
    }

    [Fact]
    public async Task Should_SendTheRestOfTheMask_OnANewConnection_AfterAnUnreadableAnswer()
    {
        // Relay 2's echo has a broken CRC: the connection may be out of step, so relay 3 goes on a new one.
        await using var board = ScriptedBoard.Start(request => request[1] switch
        {
            WaveshareApi.WriteSingleCoil when request[3] == 1 => [.. request[..6], 0, 0],
            WaveshareApi.WriteSingleCoil => request,
            _ => WaveshareModbus.WithCrc([1, 1, 1, 0b0101]),
        });
        await using var run = ClientRun.Start(board.Port, WaveshareModels.Relay8, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        await run.Client.TurnOnAsync(0b0111, Cancel);

        Assert.Equal(0b0101UL, run.Client.State.Relays);
        Assert.Contains(run.Traces, t => t.Text == "Unreadable answer: The answer's CRC is wrong." && t.IsError);
        Assert.Contains(run.Traces, t => t.Text == "Relay 3 turned on");
        Assert.Equal(2, board.Connections);
    }

    [Fact]
    public async Task Should_NeverReportARelayOn_ThatOnlyItsEchoShowed()
    {
        // The board echoes the ON but doesn't switch (as in linkage mode), and the read after it is unreadable.
        var reads = 0;
        await using var board = ScriptedBoard.Start(request =>
        {
            if (request[1] == WaveshareApi.WriteSingleCoil)
            {
                return request;
            }

            return Interlocked.Increment(ref reads) == 2
                ? WaveshareModbusTests.Bytes("01 01 01 01 90 49")
                : WaveshareModbusTests.Bytes("01 01 01 00 51 88");
        });
        await using var run = ClientRun.Start(board.Port, WaveshareModels.Relay8, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        await run.Client.TurnOnAsync(0b0001, Cancel);

        // The read at once, not in a minute, shows the truth: still off.
        await Wait.UntilAsync(() => Volatile.Read(ref reads) == 3 && run.Time.WaitsOnly(run.PollInterval));
        Assert.DoesNotContain(run.Changes, s => s.IsOn(1));
        Assert.False(run.Client.State.IsOn(1));
        Assert.True(run.Client.State.IsConnected);
        Assert.Contains(run.Traces, t => t.Text == "Unreadable answer: The answer's CRC is wrong." && t.IsError);
    }

    [Fact]
    public async Task Should_Throw_AndReadTheBoard_WhenNoRelayIsTaken()
    {
        await using var simulator = Started(WaveshareModels.Relay8);
        simulator.FailRelays = 0b0011;
        await using var run = ClientRun.Start(simulator.Port, WaveshareModels.Relay8, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        var error = await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0011, Cancel));

        // A read at once, not in a minute.
        Assert.Equal("The Waveshare board didn't take the command: The board refused it: it failed (code 4).", error.Message);
        await Wait.UntilAsync(() => simulator.ReceivedRequests.Count(r => r == ReadEightRelays) == 2);
        Assert.True(run.Client.State.IsConnected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_KeepABoardThatRefusesEveryRelay_AndLoseOneWhoseAnswersCantBeRead(bool refused)
    {
        // The first read answers; every read after it can't be read and counts. Each relay is refused with an exception
        // answer, or echoed with a broken CRC.
        var reads = 0;
        await using var board = ScriptedBoard.Start(request => request[1] switch
        {
            WaveshareApi.WriteSingleCoil when refused => WaveshareModbus.WithCrc([1, WaveshareApi.WriteSingleCoil | 0x80, 4]),
            WaveshareApi.WriteSingleCoil => [.. request[..6], 0, 0],
            _ when Interlocked.Increment(ref reads) == 1 => WaveshareModbus.WithCrc([1, 1, 1, 0]),
            _ => [1, 1, 1, 0, 0, 0],
        });
        await using var run = ClientRun.Start(board.Port, WaveshareModels.Relay8, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        for (var command = 0; command < 3; command++)
        {
            await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0011, Cancel));
        }

        // Refused: each refusal starts the count over, so the failed reads between them never reach 3.
        await Wait.UntilAsync(() => (refused ? Volatile.Read(ref reads) >= 2 : !run.Client.State.IsConnected)
            && run.Time.WaitsOnly(refused ? run.PollInterval : TimeSpan.FromSeconds(1)));
        Assert.Equal(refused, run.Client.State.IsConnected);
        Assert.Equal(!refused, run.Traces.Any(t => t.Text.StartsWith("Connection lost: ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Should_LoseTheBoard_WhenOneRelayIsRefusedAndAnothersAnswerCantBeRead()
    {
        // Relay 1 is refused, relay 2's echo has a broken CRC, and relay 3 is refused again: the last answer is a
        // refusal, but one answer couldn't be read, so each command counts. Reads as in the test above.
        var reads = 0;
        await using var board = ScriptedBoard.Start(request => request[1] switch
        {
            WaveshareApi.WriteSingleCoil when request[3] == 1 => [.. request[..6], 0, 0],
            WaveshareApi.WriteSingleCoil => WaveshareModbus.WithCrc([1, WaveshareApi.WriteSingleCoil | 0x80, 4]),
            _ when Interlocked.Increment(ref reads) == 1 => WaveshareModbus.WithCrc([1, 1, 1, 0]),
            _ => [1, 1, 1, 0, 0, 0],
        });
        await using var run = ClientRun.Start(board.Port, WaveshareModels.Relay8, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        for (var command = 0; command < 3; command++)
        {
            await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0111, Cancel));
        }

        await Wait.UntilAsync(() => !run.Client.State.IsConnected && run.Time.WaitsOnly(TimeSpan.FromSeconds(1)));
        Assert.Contains(run.Traces, t => t.Text.StartsWith("Connection lost: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_SeeChangesMadeAtTheBoard_OnTheNextPoll_AndReportOnlyThose()
    {
        await using var simulator = Started(WaveshareModels.Relay8C);
        await using var run = ClientRun.Start(simulator.Port, WaveshareModels.Relay8C);

        // Counted once the first answer was reported, when the client waits for the next poll.
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        var reported = run.Changes.Count;

        await run.PollAsync();
        await run.PollAsync();
        Assert.Equal(reported, run.Changes.Count);
        Assert.Contains(run.Traces, t => t.Text == "Ask for the relays" && t.IsRoutine);
        Assert.Contains(run.Traces, t => t.Text == "Relays: all off" && t.IsRoutine);
        Assert.Contains(run.Traces, t => t.Text == "Inputs: all off" && t.IsRoutine);

        await simulator.SwitchRelayAsync(4, true);
        await simulator.SetInputAsync(8, true);
        await run.PollAsync();

        Assert.Equal((0b1000UL, 0b1000_0000UL), (run.Client.State.Relays, run.Client.State.Inputs));
        Assert.Equal(reported + 1, run.Changes.Count);
        Assert.Contains(run.Traces, t => t.Text == "Relays: 4 on" && !t.IsRoutine);
        Assert.Contains(run.Traces, t => t.Text == "Inputs: 8 on" && !t.IsRoutine);
    }

    [Fact]
    public async Task Should_CarryOn_OnANewConnection_WhenTheBoardDropsIt()
    {
        await using var simulator = Started(WaveshareModels.Relay8);
        await using var run = ClientRun.Start(simulator.Port, WaveshareModels.Relay8);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        await Wait.UntilAsync(() => simulator.ConnectionCount == 1);

        simulator.DropConnections();
        await Wait.UntilAsync(() => simulator.ConnectionCount == 0);
        run.Time.Advance(run.PollInterval);
        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text.StartsWith("No answer: ", StringComparison.Ordinal)) && run.Time.WaitsOnly(run.PollInterval));
        Assert.True(run.Client.State.IsConnected);

        await simulator.SwitchRelayAsync(1, true);
        await run.PollAsync();

        Assert.True(run.Client.State.IsOn(1));
        Assert.Equal(1, simulator.ConnectionCount);
        Assert.DoesNotContain(run.Traces, t => t.Text.StartsWith("Connection lost", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_LoseTheBoard_AfterThreeUnansweredPolls_AndConnectAgain()
    {
        await using var simulator = Started(WaveshareModels.Relay8);
        await using var run = ClientRun.Start(simulator.Port, WaveshareModels.Relay8);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        simulator.NoAnswer = true;
        for (var poll = 1; poll <= BoardPoller<WaveshareStatus>.DefaultFailuresBeforeLost; poll++)
        {
            Assert.True(run.Client.State.IsConnected);
            var failures = poll;
            await Wait.UntilAsync(() => run.Time.WaitsOnly(run.PollInterval) && simulator.UnansweredCount == failures - 1);
            run.Time.Advance(run.PollInterval);
            await Wait.UntilAsync(() => simulator.UnansweredCount == failures && run.Time.WaitsOnly(WaveshareClient.RequestTimeout));
            run.Time.Advance(WaveshareClient.RequestTimeout);
            await Wait.UntilAsync(() => run.Traces.Count(t => t.Text == "No answer: The Waveshare board did not answer within 5 s.") == failures);
        }

        await Wait.UntilAsync(() => !run.Client.State.IsConnected);
        Assert.Contains(run.Traces, t => t.Text == "Connection lost: The Waveshare board did not answer within 5 s." && t.IsError);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0001, Cancel));

        simulator.NoAnswer = false;
        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "Trying again in 1 s") && run.Time.WaitsOnly(TimeSpan.FromSeconds(1)));
        run.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        await run.Client.TurnOnAsync(0b0001, Cancel);
        Assert.Equal(0b0001UL, simulator.Relays);
    }

    [Fact]
    public async Task Should_ThrowNotConnected_ForACommandThatGetsNoAnswer()
    {
        await using var simulator = Started(WaveshareModels.Relay8);
        await using var run = ClientRun.Start(simulator.Port, WaveshareModels.Relay8, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        simulator.NoAnswer = true;

        var sent = run.Client.TurnOnAsync(0b0011, Cancel);
        await Wait.UntilAsync(() => simulator.UnansweredCount == 1 && run.Time.PendingTimers == 2);
        run.Time.Advance(WaveshareClient.RequestTimeout);
        var error = await Assert.ThrowsAsync<BoardNotConnectedException>(() => sent);

        // Relay 2 never went: no answer stops the rest.
        Assert.Equal("The Waveshare board didn't answer; the command may not have been carried out.", error.Message);
        Assert.DoesNotContain(simulator.ReceivedRequests, r => r == "01 05 00 01 FF 00 DD FA");
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_RefuseCommands_BeforeItConnected()
    {
        using var client = CreateClient(1, new ManualTimeProvider(), WaveshareModels.Relay8);

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => client.TurnOnAsync(0b0001, Cancel));
        Assert.Throws<BoardNotConnectedException>(client.Refresh);
    }

    [Theory]
    [InlineData("01 01 01 00 51 89", "Unreadable answer: The answer's CRC is wrong.")]
    [InlineData("02 01 01 00 51 88", "Unreadable answer: The answer came from unit 2, not 1.")]
    [InlineData("01 01 08 00 00 00 00 00 00 00 00 00 00", "Unreadable answer: The answer has 8 bytes of data, not 1.")]
    [InlineData("01 81 02 C1 91", "Refused: no such address (code 2)")]
    public async Task Should_NeverConnect_WhenThePollsAnswerIsntOne(string answer, string trace)
    {
        await using var board = ScriptedBoard.Start(_ => WaveshareModbusTests.Bytes(answer));
        await using var run = ClientRun.Start(board.Port, WaveshareModels.Relay8);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == trace && t.IsError && t.Direction == BoardTraceDirection.Received));

        Assert.False(run.Client.State.IsConnected);
        Assert.Null(run.Client.State.Status);
    }

    [Fact]
    public async Task Should_SkipBytesNobodyAskedFor_BeforeTheNextRequest()
    {
        await using var board = ScriptedBoard.Start(_ => WaveshareModbusTests.Bytes("01 01 01 01 90 48 AA BB"));
        await using var run = ClientRun.Start(board.Port, WaveshareModels.Relay8);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        run.Time.Advance(run.PollInterval);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "Skipped 2 bytes nobody asked for" && t.Bytes == "AA BB"));
        await Wait.UntilAsync(() => board.Requests == 2 && run.Time.WaitsOnly(run.PollInterval));
        Assert.True(run.Client.State.IsOn(1));
    }

    [Fact]
    public async Task Should_MoveToAnotherBoard_WhenTheAddressChanges()
    {
        await using var first = Started(WaveshareModels.Relay8);
        await using var second = Started(WaveshareModels.Relay8);
        await second.SwitchRelayAsync(3, true);
        await using var run = ClientRun.Start(first.Port, WaveshareModels.Relay8);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        run.Client.SetEndpoint(new BoardEndpoint("127.0.0.1", second.Port));

        await Wait.UntilAsync(() => run.Client.State.IsOn(3));
        Assert.Equal(second.Port, run.Client.Endpoint.Port);
        await Wait.UntilAsync(() => first.ConnectionCount == 0);
    }

    [Theory]
    [InlineData("waveshare-8b")]
    [InlineData("waveshare-8c")]
    public async Task Should_SetTheRelaysToNormalMode_WhenConnecting_AndSayWhatItFound(string id)
    {
        var model = WaveshareModels.All.Single(m => m.Id == id);
        await using var simulator = Started(model);
        await simulator.SetRelayModeAsync(3, WaveshareApi.ModeLinkage);
        await simulator.SetRelayModeAsync(5, WaveshareApi.ModeToggle);
        await using var run = ClientRun.Start(simulator.Port, model);

        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.All(simulator.RelayModes, m => Assert.Equal(WaveshareApi.ModeNormal, m));
        var reset = Assert.Single(run.Resets);
        Assert.Equal(new ushort[] { 0, 0, 1, 0, 2, 0, 0, 0 }, reset.Modes);
        Assert.Equal(ReadModes, simulator.ReceivedRequests[0]);
        Assert.StartsWith("01 10 10 00 00 08 10 00 00", simulator.ReceivedRequests[1], StringComparison.Ordinal);
        Assert.Contains(run.Traces, t => t.Text == "Relay modes: 3 linkage, 5 toggle");
        Assert.Contains(run.Traces, t => t.Text == "Set every relay to normal mode");

        // The input no longer switches the relay.
        await simulator.SetInputAsync(3, true);
        Assert.Equal(0UL, simulator.Relays);
    }

    [Fact]
    public async Task Should_WriteNothing_WhenEveryRelayIsInNormalMode()
    {
        await using var simulator = Started(WaveshareModels.Relay8B);
        await using var run = ClientRun.Start(simulator.Port, WaveshareModels.Relay8B);

        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Empty(run.Resets);
        Assert.DoesNotContain(simulator.ReceivedRequests, r => r.StartsWith("01 10", StringComparison.Ordinal));
        Assert.Contains(run.Traces, t => t.Text == "Relay modes: all normal");
    }

    [Fact]
    public async Task Should_SetTheModesAgain_OnEachNewConnection()
    {
        await using var simulator = Started(WaveshareModels.Relay8B);
        await simulator.SetRelayModeAsync(1, WaveshareApi.ModeLinkage);
        await using var run = ClientRun.Start(simulator.Port, WaveshareModels.Relay8B);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Resets.Count == 1);

        // Someone set a mode with the board's own tool, and the connection broke.
        await simulator.SetRelayModeAsync(2, WaveshareApi.ModeEdge);
        simulator.NoAnswer = true;
        for (var poll = 1; poll <= BoardPoller<WaveshareStatus>.DefaultFailuresBeforeLost; poll++)
        {
            var failures = poll;
            await Wait.UntilAsync(() => run.Time.WaitsOnly(run.PollInterval) && simulator.UnansweredCount == failures - 1);
            run.Time.Advance(run.PollInterval);
            await Wait.UntilAsync(() => simulator.UnansweredCount == failures && run.Time.WaitsOnly(WaveshareClient.RequestTimeout));
            run.Time.Advance(WaveshareClient.RequestTimeout);
        }

        await Wait.UntilAsync(() => !run.Client.State.IsConnected && run.Time.WaitsOnly(TimeSpan.FromSeconds(1)));
        simulator.NoAnswer = false;
        run.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal(2, run.Resets.Count);
        Assert.Equal(new ushort[] { 0, 3, 0, 0, 0, 0, 0, 0 }, run.Resets[1].Modes);
        Assert.All(simulator.RelayModes, m => Assert.Equal(WaveshareApi.ModeNormal, m));
    }

    [Fact]
    public async Task Should_LeaveTheModesAlone_WhenTurnedOffInSettings()
    {
        await using var simulator = Started(WaveshareModels.Relay8B);
        await simulator.SetRelayModeAsync(3, WaveshareApi.ModeLinkage);
        await using var run = ClientRun.Start(simulator.Port, WaveshareModels.Relay8B, resetRelayModes: false);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        await simulator.SetInputAsync(3, true);

        Assert.Equal(WaveshareApi.ModeLinkage, simulator.RelayModes[2]);
        Assert.Equal(0b0100UL, simulator.Relays);
        Assert.Empty(run.Resets);
        Assert.DoesNotContain(simulator.ReceivedRequests, r => r.StartsWith("01 03", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("waveshare-8")]
    [InlineData("waveshare-16")]
    [InlineData("waveshare-30")]
    public async Task Should_NeverAskForModes_OnAModelWithoutInputs(string id)
    {
        var model = WaveshareModels.All.Single(m => m.Id == id);
        await using var simulator = Started(model);
        await using var run = ClientRun.Start(simulator.Port, model);

        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.All(simulator.ReceivedRequests, r => Assert.StartsWith("01 01", r, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("unreadable")]
    public async Task Should_SetTheModesAnyway_WhenTheyCantBeRead(string how)
    {
        // The modes may be anything: all eight are written to normal all the same.
        await using var board = ScriptedBoard.Start(request => request[1] switch
        {
            WaveshareApi.ReadHoldingRegisters when how == "refused" => WaveshareModbus.WithCrc([1, 0x83, 4]),
            WaveshareApi.ReadHoldingRegisters => BadCrc(WaveshareModbus.WithCrc([1, 3, 16, .. new byte[16]])),
            WaveshareApi.WriteMultipleRegisters => WaveshareModbus.WithCrc(request.AsSpan(0, 6)),
            _ => AllOff(request),
        });
        await using var run = ClientRun.Start(board.Port, WaveshareModels.Relay8B);

        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        var reset = Assert.Single(run.Resets);
        Assert.Null(reset.Modes);
        Assert.Empty(run.NotResets);
        Assert.Contains(run.Traces, t => t.Text.StartsWith("Couldn't read the relay modes: ", StringComparison.Ordinal) && t.IsError);
        Assert.Contains(run.Traces, t => t.Text == "Relay modes written");
    }

    [Fact]
    public async Task Should_ReportOnce_WhenTheBoardRefusesTheModesItCouldntRead()
    {
        await using var simulator = Started(WaveshareModels.Relay8B);
        simulator.RefuseModeReads = true;
        simulator.RefuseModeWrites = true;
        await using var run = ClientRun.Start(simulator.Port, WaveshareModels.Relay8B);

        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        await run.PollAsync();
        await run.PollAsync();

        // Once per connection, not on every poll; the board still works.
        var notReset = Assert.Single(run.NotResets);
        Assert.Equal("The board refused it: it failed (code 4).", notReset.Reason);
        Assert.Empty(run.Resets);
        Assert.Contains(run.Traces, t => t.Text == "Couldn't set the relays to normal mode: The board refused it: it failed (code 4)." && t.IsError);
        Assert.Equal(1, simulator.ReceivedRequests.Count(r => r.StartsWith("01 10", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Should_Report_WhenTheBoardRefusesTheModesItRead()
    {
        await using var simulator = Started(WaveshareModels.Relay8C);
        await simulator.SetRelayModeAsync(4, WaveshareApi.ModeToggle);
        simulator.RefuseModeWrites = true;
        await using var run = ClientRun.Start(simulator.Port, WaveshareModels.Relay8C);

        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Single(run.NotResets);
        Assert.Empty(run.Resets);
        Assert.Equal(WaveshareApi.ModeToggle, simulator.RelayModes[3]);
    }

    [Fact]
    public async Task Should_Report_WhenTheWritesAnswerIsUnreadable()
    {
        await using var board = ScriptedBoard.Start(request => request[1] switch
        {
            WaveshareApi.ReadHoldingRegisters => WaveshareModbus.WithCrc([1, 3, 16, 0, 1, .. new byte[14]]),
            WaveshareApi.WriteMultipleRegisters => [.. request.AsSpan(0, 6), 0, 0],
            _ => AllOff(request),
        });
        await using var run = ClientRun.Start(board.Port, WaveshareModels.Relay8B);

        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal("The answer's CRC is wrong.", Assert.Single(run.NotResets).Reason);
        Assert.Empty(run.Resets);
    }

    [Fact]
    public void Should_StartWithoutAnAddress()
    {
        using var client = CreateClient(WaveshareApi.DefaultPort, TimeProvider.System, WaveshareModels.Relay8, host: "");

        Assert.False(client.Endpoint.HasHost);
    }

    [Fact]
    public void Should_RefuseSettingsAndModelsItCantUse()
    {
        Assert.Throws<ArgumentException>(() => CreateClient(1, TimeProvider.System, WaveshareModels.Relay8, pollSeconds: 0));
        Assert.Throws<ArgumentException>(() => CreateClient(1, TimeProvider.System, Contactor.Boards.Vm201.Vm201Model.Model));
    }

    private static byte[] BadCrc(byte[] frame)
    {
        frame[^1] ^= 0xFF;
        return frame;
    }

    // An answer to a poll on a board whose relays and inputs are all off.
    private static byte[] AllOff(byte[] request)
    {
        return WaveshareModbus.WithCrc([1, request[1], 1, 0]);
    }

    private static WaveshareSimulator Started(BoardModel model)
    {
        var simulator = new WaveshareSimulator(model);
        simulator.Start();
        return simulator;
    }

    private static WaveshareClient CreateClient(int port, TimeProvider time, BoardModel model, int pollSeconds = 2, string host = "127.0.0.1", bool resetRelayModes = true)
    {
        var options = new WaveshareOptions { Host = host, Port = port, PollSeconds = pollSeconds, ResetRelayModes = resetRelayModes };
        return new WaveshareClient(Options.Create(options), model, time, NullLogger<WaveshareClient>.Instance);
    }

    private sealed class ClientRun : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _task;
        private readonly List<BoardTraceEntry> _traces = [];
        private readonly List<WaveshareState> _changes = [];
        private readonly List<WaveshareRelayModesReset> _resets = [];
        private readonly List<WaveshareRelayModesNotReset> _notResets = [];

        private ClientRun(WaveshareClient client, ManualTimeProvider time, TimeSpan pollInterval, CancellationTokenSource cts)
        {
            Client = client;
            Time = time;
            PollInterval = pollInterval;
            _cts = cts;
            client.Traced += (_, entry) => Add(_traces, entry);
            client.StateChanged += (_, state) => Add(_changes, state);
            client.RelayModesReset += (_, reset) => Add(_resets, reset);
            client.RelayModesNotReset += (_, notReset) => Add(_notResets, notReset);
            _task = client.RunAsync(cts.Token);
        }

        public WaveshareClient Client { get; }

        public ManualTimeProvider Time { get; }

        /// <summary>How long the client waits between polls: the one timer a test waits for while the client is idle.</summary>
        public TimeSpan PollInterval { get; }

        public List<BoardTraceEntry> Traces => Copy(_traces);

        public List<WaveshareState> Changes => Copy(_changes);

        public List<WaveshareRelayModesReset> Resets => Copy(_resets);

        public List<WaveshareRelayModesNotReset> NotResets => Copy(_notResets);

        public static ClientRun Start(int port, BoardModel model, int pollSeconds = 2, bool resetRelayModes = true)
        {
            var time = new ManualTimeProvider();
            return new ClientRun(CreateClient(port, time, model, pollSeconds, resetRelayModes: resetRelayModes), time, TimeSpan.FromSeconds(pollSeconds), new CancellationTokenSource());
        }

        /// <summary>Moves the clock to the next poll, once the client waits for it, and waits until it was answered.</summary>
        public async Task PollAsync()
        {
            var last = Client.Model.Capabilities.InputCount > 0 ? "Inputs: " : "Relays: ";
            int Answers() => Traces.Count(t => t.Direction == BoardTraceDirection.Received && t.Text.StartsWith(last, StringComparison.Ordinal));
            await Wait.UntilAsync(() => Time.WaitsOnly(PollInterval));
            var answered = Answers();
            Time.Advance(PollInterval);
            await Wait.UntilAsync(() => Answers() == answered + 1 && Time.WaitsOnly(PollInterval));
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await _task.WaitAsync(TestContext.Current.CancellationToken);
            _cts.Dispose();
            Client.Dispose();
        }

        private static void Add<T>(List<T> list, T item)
        {
            lock (list)
            {
                list.Add(item);
            }
        }

        private static List<T> Copy<T>(List<T> list)
        {
            lock (list)
            {
                return [.. list];
            }
        }
    }

    /// <summary>A board that answers each request (8 bytes, or a FC 10 with its data) with what the test's function returns.</summary>
    private sealed class ScriptedBoard : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<byte[], byte[]> _answer;
        private readonly Task _loop;
        private int _requests;
        private int _connections;

        private ScriptedBoard(TcpListener listener, Func<byte[], byte[]> answer)
        {
            _listener = listener;
            _answer = answer;
            _loop = ServeAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Requests => Volatile.Read(ref _requests);

        public int Connections => Volatile.Read(ref _connections);

        public static ScriptedBoard Start(Func<byte[], byte[]> answer)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new ScriptedBoard(listener, answer);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _loop;
            _listener.Dispose();
            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            List<Task> connections = [];
            try
            {
                while (true)
                {
                    var tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _connections);
                    connections.Add(ServeConnectionAsync(tcp));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException
                || (ex is InvalidOperationException && _stop.IsCancellationRequested))
            {
                // Stopped. A loop that was still answering when the listener stopped finds it "not listening".
            }

            await Task.WhenAll(connections);
        }

        private async Task ServeConnectionAsync(TcpClient tcp)
        {
            using (tcp)
            {
                try
                {
                    var stream = tcp.GetStream();
                    var head = new byte[8];
                    while (true)
                    {
                        await stream.ReadExactlyAsync(head, _stop.Token);
                        var request = head;
                        if (head[1] == WaveshareApi.WriteMultipleRegisters)
                        {
                            request = new byte[9 + head[6]];
                            head.CopyTo(request, 0);
                            await stream.ReadExactlyAsync(request.AsMemory(8), _stop.Token);
                        }

                        Interlocked.Increment(ref _requests);
                        await stream.WriteAsync(_answer(request), _stop.Token);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or EndOfStreamException or ObjectDisposedException)
                {
                    // The client went away or the test ended.
                }
            }
        }
    }
}
