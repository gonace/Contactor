using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Contactor.Boards;
using Contactor.Boards.Denkovi;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Denkovi;

/// <summary>
/// The client against the simulator, and against a scripted board for answers the simulator never gives. Its clock is a
/// <see cref="ManualTimeProvider"/>: a poll, a wait or a timeout only comes when the test moves the clock.
/// </summary>
public sealed class SmartDenClientTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ReportTheRelays_WithRelaySixteenAsItsOwnBit()
    {
        await using var simulator = Started();
        await simulator.SwitchRelayAsync(2, true);
        await simulator.SwitchRelayAsync(16, true);
        await using var run = ClientRun.Start(simulator.Port);

        // Connected, and the first answer reported: the client waits for the next poll.
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        Assert.Equal(new SmartDenStatus((1UL << 15) | 0b10), run.Client.State.Status);
        Assert.True(run.Client.State.IsOn(16));
        Assert.Equal(run.Time.GetUtcNow(), run.Client.State.LastContactUtc);
        Assert.Equal([ReadRelays(1)], simulator.ReceivedRequests);
        Assert.Contains(run.Changes, s => s.IsConnected && s.Relays == ((1UL << 15) | 0b10));
        Assert.Contains(run.Traces, t => t.Text == "Relays: 2 and 16 on" && t.Direction == BoardTraceDirection.Received);
    }

    [Fact]
    public async Task Should_SwitchOneRelayPerRequest_AndReadTheStateAfter()
    {
        await using var simulator = Started();
        await using var run = ClientRun.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal((1UL << 15) | 1UL, await run.Client.TurnOnAsync((1UL << 15) | 1UL, Cancel));

        // Confirmed by the read in the command itself: no time had to pass for a poll.
        Assert.Equal((1UL << 15) | 1UL, simulator.Relays);
        Assert.Equal((1UL << 15) | 1UL, run.Client.State.Relays);

        Assert.Equal(1UL, await run.Client.TurnOffAsync(1UL, Cancel));
        Assert.Equal(1UL << 15, run.Client.State.Relays);
        Assert.Equal(
            [
                ReadRelays(1),
                "00 02 00 00 00 06 00 05 00 00 FF 00",
                "00 03 00 00 00 06 00 05 00 0F FF 00",
                ReadRelays(4),
                "00 05 00 00 00 06 00 05 00 00 00 00",
                ReadRelays(6),
            ],
            simulator.ReceivedRequests);
        Assert.Contains(run.Traces, t => t.Text == "Turn on relay 16" && t.Direction == BoardTraceDirection.Sent && !t.IsRoutine);
        Assert.Contains(run.Traces, t => t.Text == "Relay 16 turned on" && t.Direction == BoardTraceDirection.Received);
        Assert.Equal(1, simulator.ConnectionCount);
    }

    [Fact]
    public async Task Should_PulseOneRelayPerRequest_AndTheBoardTurnsThemOffByItself()
    {
        var boardTime = new ManualTimeProvider();
        await using var simulator = Started(boardTime);
        await using var run = ClientRun.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal(0b0101UL, await run.Client.PulseAsync(0b0101, TimeSpan.FromMilliseconds(500), Cancel));

        Assert.Equal(0b0101UL, run.Client.State.Relays);
        Assert.Equal(
            [ReadRelays(1), "00 02 00 00 00 06 00 06 00 00 00 05", "00 03 00 00 00 06 00 06 00 02 00 05", ReadRelays(4)],
            simulator.ReceivedRequests);
        Assert.Contains(run.Traces, t => t.Text == "Pulse relay 3 for 0.5 s" && t.Direction == BoardTraceDirection.Sent);
        Assert.Contains(run.Traces, t => t.Text == "Relay 3 turned on for 0.5 s" && t.Direction == BoardTraceDirection.Received);

        // The board's own clock ends it; the app only polls.
        boardTime.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(0UL, simulator.Relays);
        await run.PollAsync();
        Assert.Equal(0UL, run.Client.State.Relays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    [InlineData(150)]
    [InlineData(6_553_600)]
    public async Task Should_RefuseAPulseLength_BeforeAnythingIsSent(int milliseconds)
    {
        await using var simulator = Started();
        await using var run = ClientRun.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.PulseAsync(0b0001, TimeSpan.FromMilliseconds(milliseconds), Cancel));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.PulseAsync(1UL << 16, TimeSpan.FromSeconds(1), Cancel));

        Assert.Equal([ReadRelays(1)], simulator.ReceivedRequests);
    }

    [Fact]
    public async Task Should_RefuseRelaysTheBoardHasNot_AndSendNothingForNone()
    {
        await using var simulator = Started();
        await using var run = ClientRun.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.TurnOnAsync(1UL << 16, Cancel));
        Assert.Equal(0UL, await run.Client.TurnOffAsync(0, Cancel));
        Assert.Equal(0UL, await run.Client.TurnOnAsync(0, Cancel));
        Assert.Equal(0UL, await run.Client.PulseAsync(0, TimeSpan.FromSeconds(1), Cancel));

        Assert.Equal([ReadRelays(1)], simulator.ReceivedRequests);
    }

    [Fact]
    public async Task Should_SendTheWholeMask_WhenTheBoardRefusesOneRelay()
    {
        await using var simulator = Started();
        await simulator.SwitchRelayAsync(2, true);
        simulator.FailRelays = 0b0010;
        await using var run = ClientRun.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        // OFF, as "All off" sends it: relay 2 is refused, relays 1 and 3 still go, and only they are returned.
        await simulator.SwitchRelayAsync(1, true);
        await simulator.SwitchRelayAsync(3, true);
        Assert.Equal(0b0101UL, await run.Client.TurnOffAsync(0b0111, Cancel));

        Assert.Equal(0b0010UL, simulator.Relays);
        Assert.Equal(0b0010UL, run.Client.State.Relays);
        Assert.Contains(run.Traces, t => t.Text == "Refused: it failed (code 4)" && t.IsError && t.Bytes == "00 03 00 00 00 03 00 85 04");
        Assert.Contains(run.Traces, t => t.Text.StartsWith("The board didn't take the command for relay 2: ", StringComparison.Ordinal) && t.IsError);
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_SendTheRestOfTheMask_OnANewConnection_AfterAnUnreadableAnswer()
    {
        // Relay 2's echo carries another transaction id: the connection may be out of step, so relay 3 goes on a new one.
        await using var board = ScriptedBoard.Start(request => request[7] switch
        {
            SmartDenApi.WriteSingleCoil when request[9] == 1 => [0xEE, 0xEE, .. request[2..]],
            SmartDenApi.WriteSingleCoil => request,
            _ => Relays(request, 0b0101),
        });
        await using var run = ClientRun.Start(board.Port, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal(0b0101UL, await run.Client.TurnOnAsync(0b0111, Cancel));

        Assert.Equal(0b0101UL, run.Client.State.Relays);
        Assert.Contains(run.Traces, t => t.Text.StartsWith("Unreadable answer: The answer is for transaction 61166", StringComparison.Ordinal) && t.IsError);
        Assert.Contains(run.Traces, t => t.Text == "Relay 3 turned on");
        Assert.Equal(2, board.Connections);
    }

    [Fact]
    public async Task Should_KeepTheStateItRead_AndReadAgainAtOnce_WhenTheReadAfterASwitchIsUnreadable()
    {
        // The board echoes the ON but the relay stays off, and the read in the command is unreadable.
        var reads = 0;
        await using var board = ScriptedBoard.Start(request =>
        {
            if (request[7] == SmartDenApi.WriteSingleCoil)
            {
                return request;
            }

            // The second read's answer carries one byte of relays, not two.
            return Interlocked.Increment(ref reads) == 2
                ? SmartDenModbus.Frame(BinaryPrimitives.ReadUInt16BigEndian(request), request[6], [SmartDenApi.ReadCoils, 1, 0])
                : Relays(request, 0);
        });
        await using var run = ClientRun.Start(board.Port, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        Assert.Equal(1UL, await run.Client.TurnOnAsync(0b0001, Cancel));

        // The read at once, not in a minute, shows the truth: still off.
        await Wait.UntilAsync(() => Volatile.Read(ref reads) == 3 && run.Time.WaitsOnly(run.PollInterval));
        Assert.DoesNotContain(run.Changes, s => s.IsOn(1));
        Assert.False(run.Client.State.IsOn(1));
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_ThrowNotConnected_NotRefused_WhenTheBoardRefusesEveryRelay()
    {
        await using var simulator = Started();
        simulator.FailRelays = 0b0011;
        await using var run = ClientRun.Start(simulator.Port, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        var error = await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0011, Cancel));

        // A read at once, not in a minute, and the board still counts as there: it answered.
        Assert.Equal("The smartDEN board didn't take the command: The board refused it: it failed (code 4).", error.Message);
        Assert.IsType<BoardRefusedException>(error.InnerException);
        await Wait.UntilAsync(() => simulator.ReceivedRequests.Count(r => r.EndsWith("00 01 00 00 00 10", StringComparison.Ordinal)) == 2);
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_LoseTheBoard_AfterThreeUnansweredPolls_AndConnectAgain()
    {
        await using var simulator = Started();
        await using var run = ClientRun.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        var noAnswer = $"The smartDEN board did not answer within {SmartDenClient.RequestTimeout.TotalSeconds} s.";

        simulator.NoAnswer = true;
        for (var poll = 1; poll <= BoardPoller<SmartDenStatus>.DefaultFailuresBeforeLost; poll++)
        {
            Assert.True(run.Client.State.IsConnected);
            var failures = poll;
            await Wait.UntilAsync(() => run.Time.WaitsOnly(run.PollInterval) && simulator.UnansweredCount == failures - 1);
            run.Time.Advance(run.PollInterval);
            await Wait.UntilAsync(() => simulator.UnansweredCount == failures && run.Time.WaitsOnly(SmartDenClient.RequestTimeout));
            run.Time.Advance(SmartDenClient.RequestTimeout);
            await Wait.UntilAsync(() => run.Traces.Count(t => t.Text == $"No answer: {noAnswer}") == failures);
        }

        await Wait.UntilAsync(() => !run.Client.State.IsConnected);
        Assert.Contains(run.Traces, t => t.Text == $"Connection lost: {noAnswer}" && t.IsError);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0001, Cancel));

        simulator.NoAnswer = false;
        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "Trying again in 1 s") && run.Time.WaitsOnly(TimeSpan.FromSeconds(1)));
        run.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        Assert.Equal(1UL, await run.Client.TurnOnAsync(0b0001, Cancel));
        Assert.Equal(1UL, simulator.Relays);
    }

    [Fact]
    public async Task Should_StayConnected_AfterOneUnansweredPoll_AndAskAgainOnANewConnection()
    {
        await using var simulator = Started();
        await using var run = ClientRun.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        simulator.NoAnswer = true;
        run.Time.Advance(run.PollInterval);
        await Wait.UntilAsync(() => simulator.UnansweredCount == 1 && run.Time.WaitsOnly(SmartDenClient.RequestTimeout));
        run.Time.Advance(SmartDenClient.RequestTimeout);
        simulator.NoAnswer = false;
        await Wait.UntilAsync(() => run.Time.WaitsOnly(run.PollInterval) && simulator.ConnectionCount == 0);

        await simulator.SwitchRelayAsync(5, true);
        await run.PollAsync();

        Assert.True(run.Client.State.IsConnected);
        Assert.True(run.Client.State.IsOn(5));
        Assert.Equal(1, simulator.ConnectionCount);
        Assert.DoesNotContain(run.Traces, t => t.Text.StartsWith("Connection lost", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_CarryOn_OnANewConnection_WhenTheBoardDropsIt()
    {
        await using var simulator = Started();
        await using var run = ClientRun.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        await Wait.UntilAsync(() => simulator.ConnectionCount == 1);

        simulator.DropConnections();
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
    public async Task Should_SendACommandAtOnce_DuringAPoll_OnTheSameConnection()
    {
        // The board takes one connection at a time: the command takes the poll's place on the connection it has, and
        // the poll's answer never comes.
        await using var simulator = Started();
        var connections = 0;
        simulator.Traced += (_, e) =>
        {
            if (e.Text.StartsWith("The app connected", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref connections);
            }
        };
        await using var run = ClientRun.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        simulator.NoAnswer = true;
        run.Time.Advance(run.PollInterval);
        await Wait.UntilAsync(() => simulator.UnansweredCount == 1 && run.Time.WaitsOnly(SmartDenClient.RequestTimeout));
        simulator.NoAnswer = false;

        Assert.Equal(1UL, await run.Client.TurnOnAsync(1, Cancel));

        Assert.Equal(1UL, simulator.Relays);
        Assert.True(run.Client.State.IsOn(1));
        Assert.Equal(1, simulator.ConnectionCount);
        Assert.Equal(1, Volatile.Read(ref connections));
        Assert.DoesNotContain(run.Traces, t => t.Text.StartsWith("No answer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_SkipThePollsLateAnswer_BeforeTheCommandsOwn()
    {
        // The poll's answer comes after all, just before the command's echo, on the same connection.
        byte[]? held = null;
        var holdReads = 0;
        await using var board = ScriptedBoard.Start(request =>
        {
            if (request[7] == SmartDenApi.WriteSingleCoil)
            {
                byte[] answer = [.. Interlocked.Exchange(ref held, null) ?? [], .. request];
                return answer;
            }

            if (Volatile.Read(ref holdReads) == 1)
            {
                Volatile.Write(ref held, Relays(request, 0b10));
                return null;
            }

            return Relays(request, 0);
        });
        await using var run = ClientRun.Start(board.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        Volatile.Write(ref holdReads, 1);
        run.Time.Advance(run.PollInterval);
        await Wait.UntilAsync(() => board.Requests == 2 && run.Time.WaitsOnly(SmartDenClient.RequestTimeout));
        Volatile.Write(ref holdReads, 0);

        Assert.Equal(1UL, await run.Client.TurnOnAsync(1, Cancel));

        Assert.Equal(1, board.Connections);
        Assert.Contains(run.Traces, t => t.Text == "Skipped a late answer nobody waited for" && t.Bytes == "00 02 00 00 00 05 00 01 02 02 00");
        Assert.True(run.Client.State.IsConnected);
        Assert.DoesNotContain(run.Changes, s => s.IsOn(2));
    }

    // Polls given up on for a command each, none of their answers ever coming: eight may wait on the connection, the
    // ninth opens it afresh, so the ids to skip never pile up.
    [Fact]
    public async Task Should_OpenANewConnection_WhenMoreThanEightPollsWereGivenUp()
    {
        var holdReads = 0;
        await using var board = ScriptedBoard.Start(request =>
            request[7] == SmartDenApi.WriteSingleCoil ? request : Volatile.Read(ref holdReads) == 1 ? null : Relays(request, 0));
        await using var run = ClientRun.Start(board.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        for (var given = 1; given <= 9; given++)
        {
            await GiveUpAPollAsync(board, run, hold => Volatile.Write(ref holdReads, hold ? 1 : 0));

            Assert.Equal(1UL, await run.Client.TurnOnAsync(1, Cancel));
            Assert.Equal(given <= 8 ? 1 : 2, board.Connections);
        }

        Assert.True(run.Client.State.IsConnected);
    }

    // A poll given up on after it had read part of what came (here the late answer of the poll before it, and the first
    // byte of its own): the connection is out of step, so the command goes on a new one, and nothing unreadable is read.
    [Fact]
    public async Task Should_OpenANewConnection_WhenAPollGivenUpOnHadReadPartOfItsAnswer()
    {
        var mode = 0;
        byte[]? late = null;
        await using var board = ScriptedBoard.Start(request =>
        {
            if (request[7] == SmartDenApi.WriteSingleCoil)
            {
                return request;
            }

            switch (Volatile.Read(ref mode))
            {
                case 1:
                    Volatile.Write(ref late, Relays(request, 0));
                    return null;
                case 2:
                    return [.. Volatile.Read(ref late)!, Relays(request, 0)[0]];
                default:
                    return Relays(request, 0);
            }
        });
        await using var run = ClientRun.Start(board.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        await GiveUpAPollAsync(board, run, hold => Volatile.Write(ref mode, hold ? 1 : 0));
        Assert.Equal(1UL, await run.Client.TurnOnAsync(1, Cancel));

        // The next poll reads the first one's late answer, then waits for the rest of its own.
        await Wait.UntilAsync(() => run.Time.WaitsOnly(run.PollInterval));
        Volatile.Write(ref mode, 2);
        run.Time.Advance(run.PollInterval);
        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "Skipped a late answer nobody waited for") && run.Time.WaitsOnly(SmartDenClient.RequestTimeout));
        Volatile.Write(ref mode, 0);

        Assert.Equal(1UL, await run.Client.TurnOffAsync(1, Cancel));

        Assert.Equal(2, board.Connections);
        Assert.DoesNotContain(run.Traces, t => t.Text.StartsWith("Unreadable answer", StringComparison.Ordinal));
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_SendEveryOff_OnANewConnection_WhenTheBoardClosedTheOldOne()
    {
        await using var simulator = Started();
        await using var run = ClientRun.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        await simulator.SwitchRelayAsync(1, true);
        await simulator.SwitchRelayAsync(2, true);
        await simulator.SwitchRelayAsync(3, true);

        // The board closes the connection, e.g. as idle; "All off" comes before the next poll.
        simulator.DropConnections();
        Assert.Equal(0b111UL, await run.Client.TurnOffAsync(0b111, Cancel));

        Assert.Equal(0UL, simulator.Relays);
        Assert.Equal(0UL, run.Client.State.Relays);
        Assert.True(run.Client.State.IsConnected);
        Assert.Equal(1, simulator.ConnectionCount);
    }

    [Fact]
    public async Task Should_ReturnWhatWasTaken_AndPollAtOnce_WhenTheReadAfterTheSwitchesGetsNoAnswer()
    {
        // The switches are answered; the read after them gets no answer on any connection.
        // The poll at once waits until the test has looked at the state the command left.
        var reads = 0;
        using var hold = new ManualResetEventSlim();
        T Held<T>(T answer)
        {
            if (Volatile.Read(ref reads) == 3)
            {
                hold.Wait(Cancel);
            }

            return answer;
        }

        await using var board = ScriptedBoard.Start(request => request[7] switch
        {
            SmartDenApi.WriteSingleCoil => request,
            _ when Interlocked.Increment(ref reads) == 2 => null,
            _ => Held(Relays(request, 1)),
        });
        await using var run = ClientRun.Start(board.Port, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        var contact = run.Client.State.LastContactUtc;

        var sent = run.Client.TurnOnAsync(1, Cancel);
        await Wait.UntilAsync(() => Volatile.Read(ref reads) == 2 && run.Time.PendingTimers == 2);
        run.Time.Advance(SmartDenClient.RequestTimeout);

        Assert.Equal(1UL, await sent);

        // Issue #379: the read after the switches failed, so the command wasn't contact with the board.
        try
        {
            await Wait.UntilAsync(() => Volatile.Read(ref reads) == 3);
            Assert.Equal(contact, run.Client.State.LastContactUtc);
        }
        finally
        {
            hold.Set();
        }

        await Wait.UntilAsync(() => Volatile.Read(ref reads) == 3 && run.Time.WaitsOnly(run.PollInterval));
        Assert.True(run.Client.State.IsOn(1));
        Assert.True(run.Client.State.IsConnected);
    }

    // Issue #396: the board is removed while a command is under way. The connection closes only once that command has
    // ended, so it finishes as sent; a command after it is refused, and nothing opens a new connection.
    [Fact]
    public async Task Should_FinishTheCommandUnderWay_BeforeTheConnectionCloses()
    {
        var written = 0;
        using var hold = new ManualResetEventSlim();
        byte[] Write(byte[] request)
        {
            Volatile.Write(ref written, 1);
            hold.Wait(Cancel);
            return request;
        }

        await using var board = ScriptedBoard.Start(request => request[7] switch
        {
            SmartDenApi.WriteSingleCoil => Write(request),
            _ => Relays(request, (ushort)Volatile.Read(ref written)),
        });
        await using var run = ClientRun.Start(board.Port, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        var sent = run.Client.TurnOnAsync(1, Cancel);
        await Wait.UntilAsync(() => Volatile.Read(ref written) == 1);

        ValueTask disposing;
        try
        {
            disposing = run.Client.DisposeAsync();
            Assert.False(disposing.IsCompleted);
            await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOffAsync(1, Cancel));
        }
        finally
        {
            hold.Set();
        }

        Assert.Equal(1UL, await sent);
        await disposing;
        Assert.True(run.Client.State.IsOn(1));
        Assert.Equal(1, board.Connections);
    }

    [Fact]
    public async Task Should_ThrowNotConnected_ForACommandThatGetsNoAnswer_AndSendNoMore()
    {
        await using var simulator = Started();
        await using var run = ClientRun.Start(simulator.Port, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        simulator.NoAnswer = true;

        var sent = run.Client.TurnOnAsync(0b0011, Cancel);
        await Wait.UntilAsync(() => simulator.UnansweredCount == 1 && run.Time.PendingTimers == 2);
        run.Time.Advance(SmartDenClient.RequestTimeout);
        var error = await Assert.ThrowsAsync<BoardNotConnectedException>(() => sent);

        // Relay 2 never went: no answer stops the rest.
        Assert.Equal("The smartDEN board didn't answer; the command may not have been carried out.", error.Message);
        Assert.DoesNotContain(simulator.ReceivedRequests, r => r.EndsWith("00 05 00 01 FF 00", StringComparison.Ordinal));
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_RefuseCommands_BeforeItConnected()
    {
        using var client = CreateClient(1, new ManualTimeProvider());

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => client.TurnOnAsync(0b0001, Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => client.TurnOffAsync(0, Cancel));
        Assert.Throws<BoardNotConnectedException>(client.Refresh);
    }

    [Theory]
    [InlineData("00 02 00 00 00 05 00 01 02 00 00", "Unreadable answer: The answer is for transaction 2, not 1.")]
    [InlineData("00 01 00 00 FF FF 00 01", "Unreadable answer: The answer's length 65535 isn't a Modbus length.")]
    [InlineData("00 01 00 00 00 04 00 01 01 00", "Unreadable answer: The answer is 4 bytes long, not 5.")]
    [InlineData("00 01 00 00 00 05 00 01 03 00 00", "Unreadable answer: The answer has 3 bytes of data, not 2.")]
    [InlineData("00 01 00 00 00 03 00 81 02", "Refused: no such address (code 2)")]
    public async Task Should_NeverConnect_WhenThePollsAnswerIsntOne(string answer, string trace)
    {
        await using var board = ScriptedBoard.Start(_ => SmartDenModbusTests.Bytes(answer));
        await using var run = ClientRun.Start(board.Port);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == trace && t.IsError && t.Direction == BoardTraceDirection.Received && t.Bytes is { Length: > 0 }));

        Assert.False(run.Client.State.IsConnected);
        Assert.Null(run.Client.State.Status);
    }

    [Fact]
    public async Task Should_SkipBytesNobodyAskedFor_BeforeTheNextRequest()
    {
        await using var board = ScriptedBoard.Start(request => [.. Relays(request, 1), 0xAA, 0xBB]);
        await using var run = ClientRun.Start(board.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        run.Time.Advance(run.PollInterval);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "Skipped 2 bytes nobody asked for" && t.Bytes == "AA BB"));
        await Wait.UntilAsync(() => board.Requests == 2 && run.Time.WaitsOnly(run.PollInterval));
        Assert.True(run.Client.State.IsOn(1));
    }

    [Fact]
    public async Task Should_MoveToAnotherBoard_WhenTheAddressChanges()
    {
        await using var first = Started();
        await using var second = Started();
        await second.SwitchRelayAsync(3, true);
        await using var run = ClientRun.Start(first.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        run.Client.SetEndpoint(new BoardEndpoint("127.0.0.1", second.Port));

        await Wait.UntilAsync(() => run.Client.State.IsOn(3));
        Assert.Equal(second.Port, run.Client.Endpoint.Port);
        await Wait.UntilAsync(() => first.ConnectionCount == 0);
    }

    [Fact]
    public void Should_StartWithoutAnAddress()
    {
        using var client = CreateClient(SmartDenApi.DefaultPort, TimeProvider.System, host: "");

        Assert.False(client.Endpoint.HasHost);
        Assert.Equal(502, client.Endpoint.Port);
    }

    [Fact]
    public void Should_RefuseSettingsAndModelsItCantUse()
    {
        Assert.Throws<ArgumentException>(() => CreateClient(1, TimeProvider.System, pollSeconds: 0));
        Assert.Throws<ArgumentException>(() => new SmartDenClient(
            Options.Create(new SmartDenOptions()),
            Contactor.Boards.Velleman.Vm201Model.Model,
            TimeProvider.System,
            NullLogger<SmartDenClient>.Instance));
    }

    // Lets the next poll go unanswered (hold), and returns once the client waits for its answer: a command then takes its
    // place and gives it up.
    private static async Task GiveUpAPollAsync(ScriptedBoard board, ClientRun run, Action<bool> hold)
    {
        await Wait.UntilAsync(() => run.Time.WaitsOnly(run.PollInterval));
        var requests = board.Requests;
        hold(true);
        run.Time.Advance(run.PollInterval);
        await Wait.UntilAsync(() => board.Requests == requests + 1 && run.Time.WaitsOnly(SmartDenClient.RequestTimeout));
        hold(false);
    }

    // The client's poll with this transaction id.
    private static string ReadRelays(int transaction)
    {
        return SmartDenModbus.Hex(SmartDenModbus.ReadCoils((ushort)transaction, 0, 16).Frame);
    }

    // An answer to a read of the 16 relays.
    private static byte[] Relays(byte[] request, ushort relays)
    {
        var transaction = BinaryPrimitives.ReadUInt16BigEndian(request);
        return SmartDenModbus.Frame(transaction, request[6], [SmartDenApi.ReadCoils, 2, (byte)relays, (byte)(relays >> 8)]);
    }

    private static SmartDenSimulator Started(TimeProvider? time = null)
    {
        var simulator = new SmartDenSimulator { TimeProvider = time ?? TimeProvider.System };
        simulator.Start();
        return simulator;
    }

    private static SmartDenClient CreateClient(int port, TimeProvider time, int pollSeconds = 2, string host = "127.0.0.1")
    {
        var options = new SmartDenOptions { Host = host, Port = port, PollSeconds = pollSeconds };
        return new SmartDenClient(Options.Create(options), SmartDenModels.Ip16RMt, time, NullLogger<SmartDenClient>.Instance);
    }

    private sealed class ClientRun : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _task;
        private readonly List<BoardTraceEntry> _traces = [];
        private readonly List<SmartDenState> _changes = [];

        private ClientRun(SmartDenClient client, ManualTimeProvider time, TimeSpan pollInterval, CancellationTokenSource cts)
        {
            Client = client;
            Time = time;
            PollInterval = pollInterval;
            _cts = cts;
            client.Traced += (_, entry) => Add(_traces, entry);
            client.StateChanged += (_, state) => Add(_changes, state);
            _task = client.RunAsync(cts.Token);
        }

        public SmartDenClient Client { get; }

        public ManualTimeProvider Time { get; }

        /// <summary>How long the client waits between polls: the one timer a test waits for while the client is idle.</summary>
        public TimeSpan PollInterval { get; }

        public List<BoardTraceEntry> Traces => Copy(_traces);

        public List<SmartDenState> Changes => Copy(_changes);

        public static ClientRun Start(int port, int pollSeconds = 2)
        {
            var time = new ManualTimeProvider();
            return new ClientRun(CreateClient(port, time, pollSeconds), time, TimeSpan.FromSeconds(pollSeconds), new CancellationTokenSource());
        }

        /// <summary>Moves the clock to the next poll, once the client waits for it, and waits until it was answered.</summary>
        public async Task PollAsync()
        {
            int Answers() => Traces.Count(t => t.Direction == BoardTraceDirection.Received && t.Text.StartsWith("Relays: ", StringComparison.Ordinal));
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

    /// <summary>
    /// A board that reads each Modbus TCP request by its header and answers it with what the test's function returns;
    /// null answers nothing.
    /// </summary>
    private sealed class ScriptedBoard : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<byte[], byte[]?> _answer;
        private readonly Task _loop;
        private int _requests;
        private int _connections;

        private ScriptedBoard(TcpListener listener, Func<byte[], byte[]?> answer)
        {
            _listener = listener;
            _answer = answer;
            _loop = ServeAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Requests => Volatile.Read(ref _requests);

        public int Connections => Volatile.Read(ref _connections);

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
                    while (true)
                    {
                        var head = new byte[SmartDenApi.HeaderBytes];
                        await stream.ReadExactlyAsync(head, _stop.Token);
                        var request = new byte[SmartDenApi.HeaderBytes - 1 + BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4))];
                        head.CopyTo(request, 0);
                        await stream.ReadExactlyAsync(request.AsMemory(SmartDenApi.HeaderBytes), _stop.Token);
                        Interlocked.Increment(ref _requests);
                        if (_answer(request) is { } answer)
                        {
                            await stream.WriteAsync(answer, _stop.Token);
                        }
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
