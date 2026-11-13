using System.Net;
using System.Net.Sockets;
using Contactor.Boards;
using Contactor.Boards.Devantech;
using Contactor.Tests.Vm208;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Devantech;

/// <summary>
/// The client against the simulator, and against a scripted board for answers the simulator never gives. Its clock is a
/// <see cref="ManualTimeProvider"/>: a poll, a wait, a timeout or the board's lock only comes when the test moves it.
/// </summary>
public sealed class DevantechClientTests
{
    private const string Apple = "79 61 70 70 6C 65";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_CheckTheModel_AndReportTheRelaysAndInputs_OfAModelWithInputs()
    {
        await using var simulator = Started(DevantechModels.Eth484);
        await simulator.SwitchRelayAsync(2, true);
        await simulator.SetInputAsync(3, true);
        await using var run = ClientRun.Start(simulator.Port, DevantechModels.Eth484);

        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal(new DevantechStatus(0b0010, 0b0100), run.Client.State.Status);
        Assert.Equal(run.Time.GetUtcNow(), run.Client.State.LastContactUtc);
        Assert.Equal(["10", "7A", "24", "25"], simulator.ReceivedRequests);
        Assert.Contains(run.Traces, t => t.Text == "Board: ETH484, hardware 2, firmware 28");
        Assert.Contains(run.Traces, t => t.Text == "No password");
        Assert.Contains(run.Changes, s => s.IsConnected && s.Relays == 0b0010 && s.Inputs == 0b0100);
    }

    [Theory]
    [InlineData("devantech-eth002")]
    [InlineData("devantech-eth008")]
    public async Task Should_AskOnlyForTheRelays_OnAModelWithoutInputs(string id)
    {
        var model = DevantechModels.All.Single(m => m.Id == id);
        await using var simulator = Started(model);
        await using var run = ClientRun.Start(simulator.Port, model);

        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal(["10", "7A", "24"], simulator.ReceivedRequests);
        Assert.Equal(0UL, run.Client.State.Inputs);
    }

    [Fact]
    public async Task Should_SwitchAndReadAllTwentyRelays()
    {
        await using var simulator = Started(DevantechModels.Eth8020);
        await simulator.SwitchRelayAsync(20, true);
        await simulator.SetInputAsync(8, true);
        await using var run = ClientRun.Start(simulator.Port, DevantechModels.Eth8020);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        Assert.True(run.Client.State.IsOn(20));
        Assert.Equal(0b1000_0000UL, run.Client.State.Inputs);

        await run.Client.TurnOnAsync(1UL << 16, Cancel);

        Assert.Equal((1UL << 16) | (1UL << 19), simulator.Relays);
        Assert.True(run.Client.State.IsOn(17));
        Assert.Contains("20 11 00", simulator.ReceivedRequests);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.TurnOnAsync(1UL << 20, Cancel));
    }

    [Fact]
    public async Task Should_SwitchOneRelayPerCommand_AndReadTheStateAfter()
    {
        await using var simulator = Started(DevantechModels.Eth008);
        await using var run = ClientRun.Start(simulator.Port, DevantechModels.Eth008);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        await run.Client.TurnOnAsync(0b0101, Cancel);

        // Confirmed by the read in the command itself: no time had to pass for a poll.
        Assert.Equal(0b0101UL, simulator.Relays);
        Assert.Equal(0b0101UL, run.Client.State.Relays);
        Assert.Contains(run.Changes, s => s.Relays == 0b0101);

        await run.Client.TurnOffAsync(0b0001, Cancel);
        Assert.Equal(0b0100UL, run.Client.State.Relays);
        Assert.Equal(["10", "7A", "24", "7A", "20 01 00", "20 03 00", "24", "7A", "21 01 00", "24"], simulator.ReceivedRequests);
        Assert.Contains(run.Traces, t => t.Text == "Turn on relay 3" && t.Direction == BoardTraceDirection.Sent && !t.IsRoutine);
        Assert.Contains(run.Traces, t => t.Text == "Relay 3 turned on" && t.Direction == BoardTraceDirection.Received);
    }

    [Fact]
    public async Task Should_RefuseRelaysTheBoardHasNot_AndSendNothingForNone()
    {
        await using var simulator = Started(DevantechModels.Eth002);
        await using var run = ClientRun.Start(simulator.Port, DevantechModels.Eth002);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.TurnOnAsync(0b100, Cancel));
        await run.Client.TurnOffAsync(0, Cancel);

        Assert.Equal(["10", "7A", "24"], simulator.ReceivedRequests);
    }

    [Fact]
    public async Task Should_SendTheWholeMask_WhenTheBoardRefusesOneRelay()
    {
        await using var simulator = Started(DevantechModels.Eth008);
        simulator.FailRelays = 0b0010;
        await using var run = ClientRun.Start(simulator.Port, DevantechModels.Eth008);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        // Only the relays it took.
        Assert.Equal(0b0101UL, await run.Client.TurnOnAsync(0b0111, Cancel));

        Assert.Equal(0b0101UL, simulator.Relays);
        Assert.Equal(0b0101UL, run.Client.State.Relays);
        Assert.Contains(run.Traces, t => t.Text == "Refused: relay 2" && t.IsError);
        Assert.Contains(run.Traces, t => t.Text == "The board didn't take the command for relay 2: The board refused relay 2." && t.IsError);
        Assert.True(run.Client.State.IsConnected);

        // OFF, as "All off" sends it: only the relays it took.
        Assert.Equal(0b0101UL, await run.Client.TurnOffAsync(0b0111, Cancel));
        Assert.Equal(0UL, simulator.Relays);
    }

    [Fact]
    public async Task Should_Throw_AndReadTheBoard_WhenNoRelayIsTaken()
    {
        await using var simulator = Started(DevantechModels.Eth008);
        simulator.FailRelays = 0b0011;
        await using var run = ClientRun.Start(simulator.Port, DevantechModels.Eth008, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.PendingTimers == 1);

        var error = await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0011, Cancel));

        // A read at once, not in a minute.
        Assert.Equal("The Devantech board didn't take the command: The board refused relay 1.", error.Message);
        await Wait.UntilAsync(() => simulator.ReceivedRequests.Count(r => r == "24") == 2);
        Assert.True(run.Client.State.IsConnected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_KeepABoardThatRefusesEveryRelay_AndLoseOneWhoseAnswersCantBeRead(bool refused)
    {
        // The first read answers; every read after it ends in a closed connection and counts. Each relay is refused
        // (01), or gets a value a relay command can't have (05).
        var reads = 0;
        await using var board = ScriptedBoard.Start(request => request switch
        {
            [0x10] => [19, 2, 28],
            [0x7A] => [0xFF],
            [0x20, _, 0] => refused ? [0x01] : [0x05],
            _ when Interlocked.Increment(ref reads) == 1 => [0],
            _ => null,
        });
        await using var run = ClientRun.Start(board.Port, DevantechModels.Eth008, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.PendingTimers == 1);

        for (var command = 0; command < 3; command++)
        {
            await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0011, Cancel));
        }

        // Refused: each refusal starts the count over, so the failed reads between them never reach 3.
        await Wait.UntilAsync(() => (refused ? Volatile.Read(ref reads) >= 2 : !run.Client.State.IsConnected) && run.Time.PendingTimers == 1);
        Assert.Equal(refused, run.Client.State.IsConnected);
        Assert.Equal(!refused, run.Traces.Any(t => t.Text.StartsWith("Connection lost: ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Should_LoseTheBoard_WhenOneRelayIsRefusedAndAnothersAnswerCantBeRead()
    {
        // Relay 1 is refused, relay 2's answer can't be read, and relay 3 is refused again: the last answer is a
        // refusal, but one answer couldn't be read, so each command counts. Reads as in the test above.
        var reads = 0;
        await using var board = ScriptedBoard.Start(request => request switch
        {
            [0x10] => [19, 2, 28],
            [0x7A] => [0xFF],
            [0x20, 2, 0] => [0x05],
            [0x20, _, 0] => [0x01],
            _ when Interlocked.Increment(ref reads) == 1 => [0],
            _ => null,
        });
        await using var run = ClientRun.Start(board.Port, DevantechModels.Eth008, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.PendingTimers == 1);

        for (var command = 0; command < 3; command++)
        {
            await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0111, Cancel));
        }

        await Wait.UntilAsync(() => !run.Client.State.IsConnected && run.Time.PendingTimers == 1);
        Assert.Contains(run.Traces, t => t.Text.StartsWith("Connection lost: ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_KeepTheLastState_AndReadAtOnce_WhenTheReadAfterTheSwitchesFails(bool wrongModel)
    {
        // Relay 2's answer can't be read, so the read after the switches opens a new connection. There 10 answers
        // another model, or 7A a value it can't have. The board's relays stay off.
        var checks = 0;
        await using var board = ScriptedBoard.Start(request => request switch
        {
            [0x10] when wrongModel && Interlocked.Increment(ref checks) == 2 => [DevantechModels.Layout(DevantechModels.Eth484).ModuleId, 2, 28],
            [0x10] => [19, 2, 28],
            [0x7A] when !wrongModel && Interlocked.Increment(ref checks) == 3 => [0x40],
            [0x7A] => [0xFF],
            [0x20, 2, 0] => [0x05],
            [0x20, _, 0] => [0x00],
            _ => [0],
        });
        await using var run = ClientRun.Start(board.Port, DevantechModels.Eth008, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.PendingTimers == 1);

        await run.Client.TurnOnAsync(0b0011, Cancel);

        // Relay 1 was taken, but only a read shows it on. The read comes at once, not in a minute, on a third connection.
        await Wait.UntilAsync(() => board.Received.Count(r => r == "24") == 2 && run.Time.PendingTimers == 1);
        Assert.Equal(3, board.Connections);
        Assert.DoesNotContain(run.Changes, s => s.Relays != 0);
        Assert.Equal(0UL, run.Client.State.Relays);
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_SendTheRestOfTheMask_OnANewConnection_AfterAnUnreadableAnswer()
    {
        // Relay 2's answer is a value a relay command can't have: the connection may be out of step, so relay 3 goes
        // on a new one, which is checked first.
        await using var board = ScriptedBoard.Start(request => request switch
        {
            [0x10] => [19, 2, 28],
            [0x7A] => [0xFF],
            [0x20, 2, 0] => [0x05],
            [0x20, _, 0] => [0x00],
            _ => [0b0101],
        });
        await using var run = ClientRun.Start(board.Port, DevantechModels.Eth008, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        await run.Client.TurnOnAsync(0b0111, Cancel);

        Assert.Equal(0b0101UL, run.Client.State.Relays);
        Assert.Contains(run.Traces, t => t.Text == "Unreadable answer: The board answered 05 to a relay command." && t.IsError && t.Bytes == "05");
        Assert.Contains(run.Traces, t => t.Text == "Relay 3 turned on");
        Assert.Equal(2, board.Connections);
    }

    [Fact]
    public async Task Should_SeeChangesMadeAtTheBoard_OnTheNextPoll_AndReportOnlyThose()
    {
        await using var simulator = Started(DevantechModels.Eth484);
        await using var run = ClientRun.Start(simulator.Port, DevantechModels.Eth484);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
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
    public async Task Should_UnlockWithThePassword_WhenConnecting_AndNeverShowIt()
    {
        var time = new ManualTimeProvider();
        await using var simulator = Started(DevantechModels.Eth008, "apple", time);
        await using var run = ClientRun.Start(simulator.Port, DevantechModels.Eth008, password: "apple", time: time);

        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        await run.Client.TurnOnAsync(0b0001, Cancel);

        Assert.Equal(0b0001UL, simulator.Relays);
        Assert.Equal(["10", "7A", DevantechTraceText.HiddenPassword, "24", "7A", "20 01 00", "24"], simulator.ReceivedRequests);
        Assert.Contains(run.Traces, t => t.Text == "Send the password" && t.Bytes == DevantechTraceText.HiddenPassword);
        Assert.Contains(run.Traces, t => t.Text == "Password accepted");
        Assert.DoesNotContain(run.Traces, t => (t.Text + t.Bytes).Contains("apple", StringComparison.Ordinal) || (t.Bytes ?? "").Contains("61 70", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_UnlockAgain_BeforeASwitch_WhenTheLockCameBack()
    {
        var time = new ManualTimeProvider();
        await using var simulator = Started(DevantechModels.Eth008, "apple", time);
        await using var run = ClientRun.Start(simulator.Port, DevantechModels.Eth008, pollSeconds: 60, password: "apple", time: time);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && time.PendingTimers == 1);

        // 31 s without a command: the board is locked again.
        time.Advance(TimeSpan.FromSeconds(31));
        await run.Client.TurnOnAsync(0b0010, Cancel);

        Assert.Equal(0b0010UL, simulator.Relays);
        Assert.Equal(["7A", DevantechTraceText.HiddenPassword, "20 02 00", "24"], simulator.ReceivedRequests.Skip(4));
        Assert.Contains(run.Traces, t => t.Text == "Locked: the board needs the password");
    }

    [Fact]
    public async Task Should_NotConnect_WithAWrongPassword_AndTryAgainLater()
    {
        var time = new ManualTimeProvider();
        await using var simulator = Started(DevantechModels.Eth008, "apple", time);
        await using var run = ClientRun.Start(simulator.Port, DevantechModels.Eth008, password: "pear", time: time);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "Trying again in 1 s"));

        Assert.False(run.Client.State.IsConnected);
        Assert.Contains(run.Traces, t => t.Text == "Password refused" && t.IsError);
        Assert.Contains(run.Traces, t => t.Text == "Connection lost: The Devantech board refused the password." && t.IsError);
        Assert.DoesNotContain(simulator.ReceivedRequests, r => r.StartsWith('2'));
    }

    [Fact]
    public async Task Should_NotConnect_WhenTheBoardAsksForAPassword_AndItHasNone()
    {
        await using var simulator = Started(DevantechModels.Eth008, "apple", new ManualTimeProvider());
        await using var run = ClientRun.Start(simulator.Port, DevantechModels.Eth008);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "Connection lost: The Devantech board asks for a password. Enter it on the Settings screen."));

        Assert.False(run.Client.State.IsConnected);
        Assert.Equal(["10", "7A"], simulator.ReceivedRequests);
    }

    [Fact]
    public async Task Should_SwitchNothing_OnAnotherModel_AndTryAgainLater()
    {
        await using var simulator = Started(DevantechModels.Eth484);
        await using var run = ClientRun.Start(simulator.Port, DevantechModels.Eth008);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "Trying again in 1 s") && run.Time.PendingTimers == 1);
        run.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => run.Traces.Count(t => t.Text == "The board at this address is an ETH484, not an ETH008.") == 2);

        Assert.False(run.Client.State.IsConnected);
        Assert.Contains(run.Traces, t => t.Text == "Connection lost: The board at this address is an ETH484, not an ETH008." && t.IsError);
        Assert.All(simulator.ReceivedRequests, r => Assert.Equal("10", r));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0001, Cancel));
    }

    [Fact]
    public async Task Should_LoseTheBoard_AfterThreeUnansweredPolls_AndConnectAgain()
    {
        await using var simulator = Started(DevantechModels.Eth008);
        await using var run = ClientRun.Start(simulator.Port, DevantechModels.Eth008);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        simulator.NoAnswer = true;
        for (var poll = 1; poll <= BoardPoller<DevantechStatus>.DefaultFailuresBeforeLost; poll++)
        {
            Assert.True(run.Client.State.IsConnected);
            var failures = poll;

            // After the first, each poll opens a new connection: its module check is what goes unanswered.
            await Wait.UntilAsync(() => run.Time.PendingTimers == 1 && simulator.UnansweredCount == failures - 1);
            run.Time.Advance(TimeSpan.FromSeconds(2));
            await Wait.UntilAsync(() => simulator.UnansweredCount == failures && run.Time.PendingTimers == 1);
            run.Time.Advance(DevantechClient.RequestTimeout);
            await Wait.UntilAsync(() => run.Traces.Count(t => t.Text == "No answer: The Devantech board did not answer within 5 s.") == failures);
        }

        await Wait.UntilAsync(() => !run.Client.State.IsConnected);
        Assert.Contains(run.Traces, t => t.Text == "Connection lost: The Devantech board did not answer within 5 s." && t.IsError);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0001, Cancel));

        simulator.NoAnswer = false;
        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "Trying again in 1 s") && run.Time.PendingTimers == 1);
        run.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        await run.Client.TurnOnAsync(0b0001, Cancel);
        Assert.Equal(0b0001UL, simulator.Relays);
    }

    [Fact]
    public async Task Should_ThrowNotConnected_ForACommandThatGetsNoAnswer()
    {
        await using var board = ScriptedBoard.Start(request => request switch
        {
            [0x10] => [19, 2, 28],
            [0x7A] => [0xFF],
            [0x20, 1, 0] => [],
            _ => [0],
        });
        await using var run = ClientRun.Start(board.Port, DevantechModels.Eth008, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.PendingTimers == 1);

        var sent = run.Client.TurnOnAsync(0b0011, Cancel);
        await Wait.UntilAsync(() => board.Received.Contains("20 01 00") && run.Time.PendingTimers == 2);
        run.Time.Advance(DevantechClient.RequestTimeout);
        var error = await Assert.ThrowsAsync<BoardNotConnectedException>(() => sent);

        // Relay 2 never went: no answer stops the rest.
        Assert.Equal("The Devantech board didn't answer; the command may not have been carried out.", error.Message);
        Assert.DoesNotContain("20 02 00", board.Received);
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_CountAShortAnswer_AsNoAnswer()
    {
        // Two bytes to the module check, which has three: the client waits for the third, and gives up.
        await using var board = ScriptedBoard.Start(_ => [19, 2]);
        await using var run = ClientRun.Start(board.Port, DevantechModels.Eth008);
        await Wait.UntilAsync(() => board.Received.Count == 1 && run.Time.PendingTimers == 1);

        run.Time.Advance(DevantechClient.RequestTimeout);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "Connection lost: The Devantech board did not answer within 5 s."));
        Assert.False(run.Client.State.IsConnected);
    }

    [Theory]
    [InlineData(0x40, "Unreadable answer: The board answered 40 to the lock check.")]
    [InlineData(0x1F, "Unreadable answer: The board answered 1F to the lock check.")]
    public async Task Should_NeverConnect_WhenTheLockCheckAnswersAValueItCantHave(byte answer, string trace)
    {
        await using var board = ScriptedBoard.Start(request => request switch
        {
            [0x10] => [19, 2, 28],
            [0x7A] => [answer],
            _ => [0],
        });
        await using var run = ClientRun.Start(board.Port, DevantechModels.Eth008);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == trace && t.IsError && t.Direction == BoardTraceDirection.Received));

        Assert.False(run.Client.State.IsConnected);
        Assert.Null(run.Client.State.Status);
    }

    [Fact]
    public async Task Should_NeverConnect_WhenThePasswordGetsAnAnswerItCantHave()
    {
        await using var board = ScriptedBoard.Start(request => request switch
        {
            [0x10] => [19, 2, 28],
            [0x7A] => [0x00],
            [0x79, ..] => [0x00],
            _ => [0],
        });
        await using var run = ClientRun.Start(board.Port, DevantechModels.Eth008, password: "apple");

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "Unreadable answer: The board answered 00 to the password."));

        Assert.False(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_SkipBytesNobodyAskedFor_BeforeTheNextRequest()
    {
        await using var board = ScriptedBoard.Start(request => request switch
        {
            [0x10] => [19, 2, 28],
            [0x7A] => [0xFF],
            _ => [0x01, 0xAA, 0xBB],
        });
        await using var run = ClientRun.Start(board.Port, DevantechModels.Eth008);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.PendingTimers == 1);
        await Wait.UntilAsync(() => board.Sent == 3);

        run.Time.Advance(TimeSpan.FromSeconds(2));

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "Skipped 2 bytes nobody asked for" && t.Bytes == "AA BB"));
        await Wait.UntilAsync(() => board.Received.Count == 4 && run.Time.PendingTimers == 1);
        Assert.True(run.Client.State.IsOn(1));
    }

    [Fact]
    public async Task Should_CarryOn_OnANewConnection_WhenTheBoardDropsIt()
    {
        await using var simulator = Started(DevantechModels.Eth008);
        await using var run = ClientRun.Start(simulator.Port, DevantechModels.Eth008);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.PendingTimers == 1);
        await Wait.UntilAsync(() => simulator.ConnectionCount == 1);

        simulator.DropConnections();
        await Wait.UntilAsync(() => simulator.ConnectionCount == 0);
        run.Time.Advance(TimeSpan.FromSeconds(2));
        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text.StartsWith("No answer: ", StringComparison.Ordinal)) && run.Time.PendingTimers == 1);
        Assert.True(run.Client.State.IsConnected);

        await simulator.SwitchRelayAsync(1, true);
        await run.PollAsync();

        Assert.True(run.Client.State.IsOn(1));
        Assert.Equal(1, simulator.ConnectionCount);
        Assert.DoesNotContain(run.Traces, t => t.Text.StartsWith("Connection lost", StringComparison.Ordinal));

        // The new connection was checked as the first was.
        Assert.Equal(2, simulator.ReceivedRequests.Count(r => r == "10"));
    }

    [Fact]
    public async Task Should_RefuseCommands_BeforeItConnected()
    {
        using var client = CreateClient(1, new ManualTimeProvider(), DevantechModels.Eth008);

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => client.TurnOnAsync(0b0001, Cancel));
        Assert.Throws<BoardNotConnectedException>(client.Refresh);
    }

    [Fact]
    public async Task Should_MoveToAnotherBoard_WhenTheAddressChanges()
    {
        await using var first = Started(DevantechModels.Eth008);
        await using var second = Started(DevantechModels.Eth008);
        await second.SwitchRelayAsync(3, true);
        await using var run = ClientRun.Start(first.Port, DevantechModels.Eth008);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        run.Client.SetEndpoint(new BoardEndpoint("127.0.0.1", second.Port));

        await Wait.UntilAsync(() => run.Client.State.IsOn(3));
        Assert.Equal(second.Port, run.Client.Endpoint.Port);
        await Wait.UntilAsync(() => first.ConnectionCount == 0);
    }

    [Fact]
    public async Task Should_ConnectAgain_WithANewPassword()
    {
        var time = new ManualTimeProvider();
        await using var simulator = Started(DevantechModels.Eth008, "apple", time);
        await using var run = ClientRun.Start(simulator.Port, DevantechModels.Eth008, password: "pear", time: time);
        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "Password refused"));

        run.Client.SetPassword("apple");

        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        Assert.Equal("apple", run.Client.Password);
        Assert.Throws<ArgumentException>(() => run.Client.SetPassword("two words"));
        Assert.Equal("apple", run.Client.Password);
    }

    [Fact]
    public void Should_StartWithoutAnAddress()
    {
        using var client = CreateClient(DevantechApi.DefaultPort, TimeProvider.System, DevantechModels.Eth008, host: "");

        Assert.False(client.Endpoint.HasHost);
        Assert.Equal(17494, client.Endpoint.Port);
    }

    [Fact]
    public void Should_RefuseSettingsAndModelsItCantUse()
    {
        Assert.Throws<ArgumentException>(() => CreateClient(1, TimeProvider.System, DevantechModels.Eth008, pollSeconds: 0));
        Assert.Throws<ArgumentException>(() => CreateClient(1, TimeProvider.System, Contactor.Boards.Vm201.Vm201Model.Model));
        var error = Assert.Throws<ArgumentException>(() => CreateClient(1, TimeProvider.System, DevantechModels.Eth008, password: "smörgås"));
        Assert.DoesNotContain("smörgås", error.Message, StringComparison.Ordinal);
    }

    private static DevantechSimulator Started(BoardModel model, string? password = null, TimeProvider? time = null)
    {
        var simulator = new DevantechSimulator(model, password, NullLogger.Instance) { TimeProvider = time ?? TimeProvider.System };
        simulator.Start();
        return simulator;
    }

    private static DevantechClient CreateClient(int port, TimeProvider time, BoardModel model, int pollSeconds = 2, string host = "127.0.0.1", string password = "")
    {
        var options = new DevantechOptions { Host = host, Port = port, PollSeconds = pollSeconds, Password = password };
        return new DevantechClient(Options.Create(options), model, time, NullLogger<DevantechClient>.Instance);
    }

    private sealed class ClientRun : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _task;
        private readonly List<BoardTraceEntry> _traces = [];
        private readonly List<DevantechState> _changes = [];

        private ClientRun(DevantechClient client, ManualTimeProvider time, CancellationTokenSource cts)
        {
            Client = client;
            Time = time;
            _cts = cts;
            client.Traced += (_, entry) => Add(_traces, entry);
            client.StateChanged += (_, state) => Add(_changes, state);
            _task = client.RunAsync(cts.Token);
        }

        public DevantechClient Client { get; }

        public ManualTimeProvider Time { get; }

        public List<BoardTraceEntry> Traces => Copy(_traces);

        public List<DevantechState> Changes => Copy(_changes);

        public static ClientRun Start(int port, BoardModel model, int pollSeconds = 2, string password = "", ManualTimeProvider? time = null)
        {
            time ??= new ManualTimeProvider();
            return new ClientRun(CreateClient(port, time, model, pollSeconds, password: password), time, new CancellationTokenSource());
        }

        /// <summary>Moves the clock to the next poll, once the client waits for it, and waits until it was answered.</summary>
        public async Task PollAsync()
        {
            var last = Client.Model.Capabilities.InputCount > 0 ? "Inputs: " : "Relays: ";
            int Answers() => Traces.Count(t => t.Direction == BoardTraceDirection.Received && t.Text.StartsWith(last, StringComparison.Ordinal));
            await Wait.UntilAsync(() => Time.PendingTimers == 1);
            var answered = Answers();
            Time.Advance(TimeSpan.FromSeconds(2));
            await Wait.UntilAsync(() => Answers() == answered + 1 && Time.PendingTimers == 1);
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

    /// <summary>
    /// A board that answers each packet with what the test's function returns for it; nothing for an empty answer.
    /// </summary>
    private sealed class ScriptedBoard : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<byte[], byte[]?> _answer;
        private readonly Task _loop;
        private readonly List<string> _received = [];
        private int _sent;
        private int _connections;

        private ScriptedBoard(TcpListener listener, Func<byte[], byte[]?> answer)
        {
            _listener = listener;
            _answer = answer;
            _loop = ServeAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>The packets received, as hex.</summary>
        public List<string> Received
        {
            get
            {
                lock (_received)
                {
                    return [.. _received];
                }
            }
        }

        /// <summary>How many answers it sent.</summary>
        public int Sent => Volatile.Read(ref _sent);

        public int Connections => Volatile.Read(ref _connections);

        /// <param name="answer">The answer to a packet: empty for none, null to close the connection.</param>
        public static ScriptedBoard Start(Func<byte[], byte[]?> answer)
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
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Stopped.
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
                    var buffer = new byte[64];
                    while (true)
                    {
                        var read = await stream.ReadAsync(buffer, _stop.Token);
                        if (read == 0)
                        {
                            return;
                        }

                        var request = buffer[..read];
                        var answer = _answer(request);
                        lock (_received)
                        {
                            _received.Add(DevantechProtocol.Hex(request));
                        }

                        if (answer is null)
                        {
                            return;
                        }

                        if (answer.Length > 0)
                        {
                            await stream.WriteAsync(answer, _stop.Token);
                            Interlocked.Increment(ref _sent);
                        }
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
                {
                    // The client went away or the test ended.
                }
            }
        }
    }
}
