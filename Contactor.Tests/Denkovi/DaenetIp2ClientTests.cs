using System.Net;
using System.Net.Sockets;
using Contactor.Boards;
using Contactor.Boards.Denkovi;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Denkovi;

/// <summary>
/// The client against the simulator, and against a scripted board for answers the simulator never gives. Its clock is a
/// <see cref="ManualTimeProvider"/>: a poll, a wait or a timeout only comes when the test moves the clock.
/// </summary>
public sealed class DaenetIp2ClientTests
{
    private const string Community = DaenetIp2Api.FactoryReadWriteCommunity;

    private static readonly string NoAnswer =
        $"The DAEnetIP2 v2 board didn't answer within {DaenetIp2Client.RequestTimeout.TotalSeconds} s, asked 2 times: check the address and the community.";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ReportTheRelays_FromP3AndP5_OnTheSixteenRelayKit()
    {
        await using var simulator = Started(DaenetIp2Models.Relays16);
        await simulator.SwitchRelayAsync(2, true);
        await simulator.SwitchRelayAsync(16, true);
        await using var run = ClientRun.Start(simulator.Port, DaenetIp2Models.Relays16);

        // Answered, so connected: the client waits for the next poll.
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        Assert.Equal(new DaenetIp2Status((1UL << 15) | 0b10), run.Client.State.Status);
        Assert.Equal(run.Time.GetUtcNow(), run.Client.State.LastContactUtc);
        Assert.Equal([Get(1, DaenetIp2Port.P3), Get(2, DaenetIp2Port.P5)], simulator.ReceivedRequests);
        Assert.Contains(run.Changes, s => s.IsConnected && s.Relays == ((1UL << 15) | 0b10));
        Assert.Contains(run.Traces, t => t.Text == "Ask for port P5" && t.Direction == BoardTraceDirection.Sent && t.IsRoutine && t.Bytes == Get(2, DaenetIp2Port.P5));
        Assert.Contains(run.Traces, t => t.Text == "Port P3: pin 2 high" && t.Direction == BoardTraceDirection.Received && t.Bytes == Answer(1, DaenetIp2Port.P3, 0b10));
        Assert.Contains(run.Traces, t => t.Text == "Relays: 2 and 16 on" && t.Direction == BoardTraceDirection.Received && !t.IsRoutine);
    }

    [Fact]
    public async Task Should_ReadOnlyP5_OnTheTwoRelayKit_WithRelayOneOnP5Point7()
    {
        await using var simulator = Started(DaenetIp2Models.Relays2);
        await simulator.SwitchRelayAsync(1, true);
        await using var run = ClientRun.Start(simulator.Port, DaenetIp2Models.Relays2);

        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        Assert.Equal(0b01UL, run.Client.State.Relays);
        Assert.Equal(0b0100_0000, simulator.P5);
        Assert.Equal([Get(1, DaenetIp2Port.P5)], simulator.ReceivedRequests);
    }

    [Fact]
    public async Task Should_SwitchOneRelayPerSet_AndReadTheStateAfter()
    {
        await using var simulator = Started(DaenetIp2Models.Relays16);
        await using var run = ClientRun.Start(simulator.Port, DaenetIp2Models.Relays16);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal((1UL << 15) | 1UL, await run.Client.TurnOnAsync((1UL << 15) | 1UL, Cancel));

        // Confirmed by the read in the command itself: no time had to pass for a poll.
        Assert.Equal((1UL << 15) | 1UL, simulator.Relays);
        Assert.Equal((1UL << 15) | 1UL, run.Client.State.Relays);

        Assert.Equal(1UL, await run.Client.TurnOffAsync(1UL, Cancel));
        Assert.Equal(1UL << 15, run.Client.State.Relays);
        Assert.Equal(
            [
                Get(1, DaenetIp2Port.P3),
                Get(2, DaenetIp2Port.P5),
                Set(3, DaenetIp2Port.P3, 1, high: true),
                Set(4, DaenetIp2Port.P5, 8, high: true),
                Get(5, DaenetIp2Port.P3),
                Get(6, DaenetIp2Port.P5),
                Set(7, DaenetIp2Port.P3, 1, high: false),
                Get(8, DaenetIp2Port.P3),
                Get(9, DaenetIp2Port.P5),
            ],
            simulator.ReceivedRequests);
        Assert.Contains(run.Traces, t => t.Text == "Turn on relay 16 (P5.8)" && t.Direction == BoardTraceDirection.Sent && !t.IsRoutine);
        Assert.Contains(run.Traces, t => t.Text == "Relay 16 turned on" && t.Direction == BoardTraceDirection.Received);
        Assert.Contains(run.Traces, t => t.Text == "Relay 1 turned off" && t.Direction == BoardTraceDirection.Received);
    }

    [Fact]
    public async Task Should_RefuseRelaysTheKitHasNot_AndSendNothingForNone()
    {
        await using var simulator = Started(DaenetIp2Models.Relays8);
        await using var run = ClientRun.Start(simulator.Port, DaenetIp2Models.Relays8);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.TurnOnAsync(1UL << 8, Cancel));
        Assert.Equal(0UL, await run.Client.TurnOffAsync(0, Cancel));
        Assert.Equal(0UL, await run.Client.TurnOnAsync(0, Cancel));

        Assert.Equal([Get(1, DaenetIp2Port.P5)], simulator.ReceivedRequests);
    }

    [Fact]
    public async Task Should_SendTheWholeMask_WhenTheBoardRefusesOneRelay()
    {
        await using var simulator = Started(DaenetIp2Models.Relays8);
        await simulator.SwitchRelayAsync(2, true);
        simulator.FailRelays = 0b0010;
        await using var run = ClientRun.Start(simulator.Port, DaenetIp2Models.Relays8);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        // OFF, as "All off" sends it: relay 2 is refused, relays 1 and 3 still go, and only they are returned.
        await simulator.SwitchRelayAsync(1, true);
        await simulator.SwitchRelayAsync(3, true);
        Assert.Equal(0b0101UL, await run.Client.TurnOffAsync(0b0111, Cancel));

        Assert.Equal(0b0010UL, simulator.Relays);
        Assert.Equal(0b0010UL, run.Client.State.Relays);
        Assert.Contains(run.Traces, t => t.Text == "Refused: it failed (error 5)" && t.IsError && t.Bytes is { Length: > 0 });
        Assert.Contains(run.Traces, t => t.Text == "The board didn't take the command for relay 2: The board refused it: it failed (error 5)." && t.IsError);
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_ThrowNotConnected_NotRefused_WhenTheBoardRefusesEveryRelay()
    {
        await using var simulator = Started(DaenetIp2Models.Relays8);
        simulator.FailRelays = 0b0011;
        await using var run = ClientRun.Start(simulator.Port, DaenetIp2Models.Relays8, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        var error = await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0011, Cancel));

        // A read at once, not in a minute, and the board still counts as there: it answered.
        Assert.Equal("The DAEnetIP2 v2 board didn't take the command: The board refused it: it failed (error 5).", error.Message);
        Assert.IsType<BoardRefusedException>(error.InnerException);
        await Wait.UntilAsync(() => simulator.ReceivedRequests.Count(r => r.StartsWith("A0", StringComparison.Ordinal)) == 2);
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_AskAgain_WithTheSameRequestId_WhenAnAnswerIsLost()
    {
        await using var simulator = Started(DaenetIp2Models.Relays8);
        await using var run = ClientRun.Start(simulator.Port, DaenetIp2Models.Relays8);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        simulator.DropRequests = 1;
        await simulator.SwitchRelayAsync(4, true);
        run.Time.Advance(run.PollInterval);
        await Wait.UntilAsync(() => simulator.UnansweredCount == 1 && run.Time.WaitsOnly(DaenetIp2Client.RequestTimeout));
        run.Time.Advance(DaenetIp2Client.RequestTimeout);
        await Wait.UntilAsync(() => run.Client.State.IsOn(4) && run.Time.WaitsOnly(run.PollInterval));

        Assert.Equal([Get(1, DaenetIp2Port.P5), Get(2, DaenetIp2Port.P5), Get(2, DaenetIp2Port.P5)], simulator.ReceivedRequests);
        Assert.Contains(run.Traces, t => t.Text == "No answer within 1 s: asking again" && t.IsError);
        Assert.DoesNotContain(run.Traces, t => t.Text.StartsWith("No answer: ", StringComparison.Ordinal));
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_LoseTheBoard_AfterThreeUnansweredPolls_AndAnswerAgain()
    {
        await using var simulator = Started(DaenetIp2Models.Relays8);
        await using var run = ClientRun.Start(simulator.Port, DaenetIp2Models.Relays8);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        simulator.NoAnswer = true;
        for (var poll = 1; poll <= BoardPoller<DaenetIp2Status>.DefaultFailuresBeforeLost; poll++)
        {
            Assert.True(run.Client.State.IsConnected);
            var sent = 2 * (poll - 1);
            await Wait.UntilAsync(() => run.Time.WaitsOnly(run.PollInterval) && simulator.UnansweredCount == sent);
            run.Time.Advance(run.PollInterval);

            // Each poll is asked twice before it counts as unanswered.
            await Wait.UntilAsync(() => simulator.UnansweredCount == sent + 1 && run.Time.WaitsOnly(DaenetIp2Client.RequestTimeout));
            run.Time.Advance(DaenetIp2Client.RequestTimeout);
            await Wait.UntilAsync(() => simulator.UnansweredCount == sent + 2 && run.Time.WaitsOnly(DaenetIp2Client.RequestTimeout));
            run.Time.Advance(DaenetIp2Client.RequestTimeout);
            var failures = poll;
            await Wait.UntilAsync(() => run.Traces.Count(t => t.Text == $"No answer: {NoAnswer}") == failures);
        }

        await Wait.UntilAsync(() => !run.Client.State.IsConnected);
        Assert.Contains(run.Traces, t => t.Text == $"Connection lost: {NoAnswer}" && t.IsError);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0001, Cancel));

        // The board answers again: the next try finds it.
        simulator.NoAnswer = false;
        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "Trying again in 1 s") && run.Time.WaitsOnly(TimeSpan.FromSeconds(1)));
        run.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        Assert.Equal(1UL, await run.Client.TurnOnAsync(0b0001, Cancel));
        Assert.Equal(1UL, simulator.Relays);
    }

    [Fact]
    public async Task Should_NeverConnect_WithAWrongCommunity_AndConnectOnceItIsRight()
    {
        await using var simulator = new DaenetIp2Simulator(DaenetIp2Models.Relays8, "apple", NullLogger.Instance);
        simulator.Start();
        await simulator.SwitchRelayAsync(2, true);
        await using var run = ClientRun.Start(simulator.Port, DaenetIp2Models.Relays8);

        // The board doesn't answer a wrong community: the trace says what to check.
        await Wait.UntilAsync(() => simulator.UnansweredCount == 1 && run.Time.WaitsOnly(DaenetIp2Client.RequestTimeout));
        run.Time.Advance(DaenetIp2Client.RequestTimeout);
        await Wait.UntilAsync(() => simulator.UnansweredCount == 2 && run.Time.WaitsOnly(DaenetIp2Client.RequestTimeout));
        run.Time.Advance(DaenetIp2Client.RequestTimeout);
        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == $"No answer: {NoAnswer}" && t.IsError) && run.Time.WaitsOnly(run.PollInterval));
        Assert.False(run.Client.State.IsConnected);
        Assert.Null(run.Client.State.Status);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(1, Cancel));

        run.Client.SetCommunity("apple");

        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        Assert.True(run.Client.State.IsOn(2));
        Assert.Equal("apple", run.Client.Community);
    }

    [Fact]
    public async Task Should_StopAfterTwoRelaysInARowWithoutAnAnswer_AndThrowNotConnected()
    {
        await using var simulator = Started(DaenetIp2Models.Relays8);
        await using var run = ClientRun.Start(simulator.Port, DaenetIp2Models.Relays8, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        var start = run.Time.GetUtcNow();
        simulator.NoAnswer = true;

        // Each set is asked twice; relay 2 goes after relay 1 got none, but relay 3 doesn't: the board is gone.
        var sent = run.Client.TurnOnAsync(0b0111, run.Stopping);
        for (var unanswered = 1; unanswered <= 4; unanswered++)
        {
            var count = unanswered;
            await Wait.UntilAsync(() => simulator.UnansweredCount == count && run.Time.PendingTimers == 2);
            run.Time.Advance(DaenetIp2Client.RequestTimeout);
        }

        var error = await Assert.ThrowsAsync<BoardNotConnectedException>(() => Done(sent));

        Assert.Equal("The DAEnetIP2 v2 board didn't answer; the command may not have been carried out.", error.Message);
        Assert.Equal(4 * DaenetIp2Client.RequestTimeout, run.Time.GetUtcNow() - start);
        Assert.Equal(4, simulator.ReceivedRequests.Count(r => r.StartsWith("A3", StringComparison.Ordinal)));
        Assert.Equal(2, simulator.ReceivedRequests.Count(r => r.StartsWith("A3", StringComparison.Ordinal) && r.Contains("09 02 02 02 00", StringComparison.Ordinal)));
        Assert.DoesNotContain(simulator.ReceivedRequests, r => r.Contains("09 02 02 03 00", StringComparison.Ordinal));
        Assert.Contains(run.Traces, t => t.Text == "No answer for relay 1: going on with the rest" && t.IsError);
        Assert.Contains(run.Traces, t => t.Text == "No answer for relay 2 either: the board is most likely gone, so the rest isn't sent" && t.IsError);
        Assert.True(run.Client.State.IsConnected);
    }

    // Relay 1 gets no answer, relay 2 is taken, relay 3 gets none either: the taken relay in between starts the count
    // over, so relay 4 still goes.
    [Fact]
    public async Task Should_CountOnlyRelaysInARow_WithoutAnAnswer()
    {
        await using var simulator = Started(DaenetIp2Models.Relays8);
        simulator.SilentRelays = 0b0101;
        await using var run = ClientRun.Start(simulator.Port, DaenetIp2Models.Relays8, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        var sent = run.Client.TurnOnAsync(0b1111, run.Stopping);
        for (var unanswered = 1; unanswered <= 4; unanswered++)
        {
            var count = unanswered;
            await Wait.UntilAsync(() => simulator.UnansweredCount == count && run.Time.PendingTimers == 2);
            run.Time.Advance(DaenetIp2Client.RequestTimeout);
        }

        Assert.Equal(0b1010UL, await Done(sent));
        Assert.Equal(0b1010UL, simulator.Relays);
        Assert.Contains(run.Traces, t => t.Text == "No answer for relay 3: going on with the rest" && t.IsError);
        Assert.DoesNotContain(run.Traces, t => t.Text.Contains("either", StringComparison.Ordinal));
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_ReturnWhatWasTaken_WithoutTheRead_WhenTheBoardGoesInTheMiddleOfAMask()
    {
        // Relay 1 is taken; then the board is gone: relays 2 and 3 get no answer, relay 4 and the read after aren't sent.
        var gone = 0;
        var silent = 0;
        await using var board = ScriptedBoard.Start(request =>
        {
            if (Volatile.Read(ref gone) == 1)
            {
                Interlocked.Increment(ref silent);
                return [];
            }

            if (request.PduType == DaenetIp2Api.SetRequest)
            {
                Volatile.Write(ref gone, 1);
                return [Echo(request)];
            }

            return [Reply(request, 0)];
        });
        await using var run = ClientRun.Start(board.Port, DaenetIp2Models.Relays8, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        var start = run.Time.GetUtcNow();

        var sent = run.Client.TurnOnAsync(0b1111, run.Stopping);
        for (var unanswered = 1; unanswered <= 4; unanswered++)
        {
            var count = unanswered;
            await Wait.UntilAsync(() => Volatile.Read(ref silent) == count && run.Time.PendingTimers == 2);
            run.Time.Advance(DaenetIp2Client.RequestTimeout);
        }

        // Returned without another try: a read in the command would wait for the clock.
        Assert.Equal(0b0001UL, await Done(sent));
        Assert.Equal(4 * DaenetIp2Client.RequestTimeout, run.Time.GetUtcNow() - start);
        Assert.DoesNotContain(run.Traces, t => t.Text == "Turn on relay 4 (P5.4)");
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_SendEveryOtherOff_WhenOneRelaysSetGetsNoAnswer()
    {
        // "All off" on the 16-relay kit, and relay 5's set is lost every time: the 15 others still go off.
        await using var simulator = Started(DaenetIp2Models.Relays16);
        for (var relay = 1; relay <= 16; relay++)
        {
            await simulator.SwitchRelayAsync(relay, true);
        }

        simulator.SilentRelays = 1UL << 4;
        await using var run = ClientRun.Start(simulator.Port, DaenetIp2Models.Relays16, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        var sent = run.Client.TurnOffAsync(0xFFFF, run.Stopping);
        await Wait.UntilAsync(() => simulator.UnansweredCount == 1 && run.Time.PendingTimers == 2);
        run.Time.Advance(DaenetIp2Client.RequestTimeout);
        await Wait.UntilAsync(() => simulator.UnansweredCount == 2 && run.Time.PendingTimers == 2);
        run.Time.Advance(DaenetIp2Client.RequestTimeout);

        Assert.Equal(0xFFFFUL & ~(1UL << 4), await Done(sent));
        Assert.Equal(1UL << 4, simulator.Relays);
        Assert.Equal(1UL << 4, run.Client.State.Relays);
        Assert.Equal(17, simulator.ReceivedRequests.Count(r => r.StartsWith("A3", StringComparison.Ordinal)));
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_NeverTakeTheAnswer_FromAnotherAddress()
    {
        // macOS has only 127.0.0.1 until another loopback address is added (sudo ifconfig lo0 alias 127.0.0.2).
        Assert.SkipUnless(CanBindUdp(IPAddress.Parse("127.0.0.2")), "This computer can't bind to 127.0.0.2.");

        // The right answer, with the right id and community, from the board's port but only ever from another address.
        await using var board = ScriptedBoard.Start(
            request => [new Datagram(DaenetIp2Snmp.Response(request.RequestId, Community, request.Oid, 0b0101), FromElsewhere: true)],
            elsewhere: IPAddress.Parse("127.0.0.2"));
        await using var run = ClientRun.Start(board.Port, DaenetIp2Models.Relays8);

        Assert.Equal(board.Port, board.ElsewherePort);
        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == $"Skipped a datagram from 127.0.0.2:{board.Port}: not the board")
            && run.Time.WaitsOnly(DaenetIp2Client.RequestTimeout));

        Assert.False(run.Client.State.IsConnected);
        Assert.Null(run.Client.State.Status);
    }

    [Fact]
    public async Task Should_OpenANewSocket_AfterOneTheNetworkRefused()
    {
        // Sending to the broadcast address without the right to is refused at once, on every system.
        await using var run = ClientRun.Start(9, DaenetIp2Models.Relays8, host: "255.255.255.255");
        static bool IsOpened(BoardTraceEntry t) => t.Text.StartsWith("Sending to 255.255.255.255:9 from UDP port ", StringComparison.Ordinal);

        await Wait.UntilAsync(() => run.Traces.Count(t => t.Text.StartsWith("No answer: ", StringComparison.Ordinal)) == 1 && run.Time.WaitsOnly(run.PollInterval));
        Assert.Equal(1, run.Traces.Count(IsOpened));

        run.Time.Advance(run.PollInterval);

        await Wait.UntilAsync(() => run.Traces.Count(IsOpened) == 2);
    }

    [Fact]
    public async Task Should_ReturnWhatWasTaken_AndPollAtOnce_WhenTheReadAfterTheSwitchesGetsNoAnswer()
    {
        // The set is answered; both tries of the read after it get no answer.
        var reads = 0;
        await using var board = ScriptedBoard.Start(request => request.PduType == DaenetIp2Api.SetRequest
            ? [Echo(request)]
            : Interlocked.Increment(ref reads) is 2 or 3 ? [] : [Reply(request, 1)]);
        await using var run = ClientRun.Start(board.Port, DaenetIp2Models.Relays8, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        var sent = run.Client.TurnOnAsync(1, run.Stopping);
        await Wait.UntilAsync(() => Volatile.Read(ref reads) == 2 && run.Time.PendingTimers == 2);
        run.Time.Advance(DaenetIp2Client.RequestTimeout);
        await Wait.UntilAsync(() => Volatile.Read(ref reads) == 3 && run.Time.PendingTimers == 2);
        run.Time.Advance(DaenetIp2Client.RequestTimeout);

        Assert.Equal(1UL, await Done(sent));
        await Wait.UntilAsync(() => Volatile.Read(ref reads) == 4 && run.Time.WaitsOnly(run.PollInterval));
        Assert.True(run.Client.State.IsOn(1));
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_SendTheRestOfTheMask_AfterAnEchoItCantRead()
    {
        // Relay 2's set is echoed with another value; the board still takes relays 1 and 3.
        await using var board = ScriptedBoard.Start(request => request.PduType switch
        {
            DaenetIp2Api.SetRequest when request.Oid[^2] == 2 => [Reply(request, 0)],
            DaenetIp2Api.SetRequest => [Echo(request)],
            _ => [Reply(request, 0b0101)],
        });
        await using var run = ClientRun.Start(board.Port, DaenetIp2Models.Relays8, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal(0b0101UL, await run.Client.TurnOnAsync(0b0111, Cancel));

        Assert.Equal(0b0101UL, run.Client.State.Relays);
        Assert.Contains(run.Traces, t => t.Text == "Unreadable answer: The answer doesn't confirm what was set: 0, not 1." && t.IsError);
        Assert.Contains(run.Traces, t => t.Text == "Relay 3 turned on");
    }

    [Fact]
    public async Task Should_SkipLateAnswers_DatagramsFromElsewhere_AndOnesItCantRead()
    {
        // Before each answer: the answer to an earlier request, the right answer from another address, and two bytes.
        await using var board = ScriptedBoard.Start(request =>
        [
            new Datagram(DaenetIp2Snmp.Response(request.RequestId + 1000, Community, request.Oid, 0xFF)),
            new Datagram(DaenetIp2Snmp.Response(request.RequestId, Community, request.Oid, 0xFF), FromElsewhere: true),
            new Datagram([0x30, 0x05]),
            Reply(request, 0b0101),
        ]);
        await using var run = ClientRun.Start(board.Port, DaenetIp2Models.Relays8);

        await Wait.UntilAsync(() => run.Client.State.IsConnected
            && run.Traces.Any(t => t.Text == "Skipped a late answer nobody waited for" && t.IsRoutine && t.Bytes is { Length: > 0 })
            && run.Traces.Any(t => t.Text == $"Skipped a datagram from 127.0.0.1:{board.ElsewherePort}: not the board" && t.IsError)
            && run.Traces.Any(t => t.Text == "Skipped an unreadable datagram of 2 bytes: The message is cut short." && t.IsError && t.Bytes is null));

        Assert.Equal(0b0101UL, run.Client.State.Relays);
        Assert.DoesNotContain(run.Changes, s => s.Relays == 0xFF);
    }

    [Fact]
    public async Task Should_TraceOnlyTheFirstSkippedDatagrams_OfAFlood()
    {
        // Twenty answers from another address before each real one.
        await using var board = ScriptedBoard.Start(request =>
        [
            .. Enumerable.Range(0, 20).Select(_ => new Datagram(DaenetIp2Snmp.Response(request.RequestId, Community, request.Oid, 0xFF), FromElsewhere: true)),
            Reply(request, 0b0101),
        ]);
        await using var run = ClientRun.Start(board.Port, DaenetIp2Models.Relays8);

        await Wait.UntilAsync(() => run.Client.State.IsConnected
            && run.Traces.Any(t => t.Text == "Skipping more datagrams that aren't the answer" && t.IsError));

        Assert.Equal(DaenetIp2Client.MaxSkippedTraced, run.Traces.Count(t => t.Text.StartsWith("Skipped a datagram from ", StringComparison.Ordinal)));
        Assert.Equal(0b0101UL, run.Client.State.Relays);
    }

    [Fact]
    public async Task Should_SendNothing_WithoutAnAddress()
    {
        await using var run = ClientRun.Start(DaenetIp2Api.DefaultPort, DaenetIp2Models.Relays8, host: "");

        // An empty name would find this computer itself: the try fails before anything is sent.
        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text.StartsWith("Connection lost: ", StringComparison.Ordinal)));

        Assert.False(run.Client.State.IsConnected);
        Assert.DoesNotContain(run.Traces, t => t.Direction == BoardTraceDirection.Sent);
    }

    public static TheoryData<string, string> NotAnswers { get; } = new()
    {
        { "community", "Unreadable answer: The answer carries another community." },
        { "oid", "Unreadable answer: The answer is for .1.3.6.1.4.1.42505.9.2.1.33.0, not .1.3.6.1.4.1.42505.9.2.2.33.0." },
        { "value", "Unreadable answer: The answer's value 256 isn't from 0 to 255." },
        { "refused", "Refused: no such name (error 2)" },
    };

    [Theory]
    [MemberData(nameof(NotAnswers))]
    public async Task Should_NeverConnect_WhenThePollsAnswerIsntOne(string wrong, string trace)
    {
        await using var board = ScriptedBoard.Start(request => wrong switch
        {
            "community" => [new Datagram(DaenetIp2Snmp.Response(request.RequestId, "public", request.Oid, 1))],
            "oid" => [new Datagram(DaenetIp2Snmp.Response(request.RequestId, Community, DaenetIp2Api.PortOid(DaenetIp2Port.P3), 1))],
            "value" => [Reply(request, 256)],
            _ => [new Datagram(DaenetIp2Snmp.Response(request.RequestId, Community, request.Oid, null, DaenetIp2Api.NoSuchName, 1))],
        });
        await using var run = ClientRun.Start(board.Port, DaenetIp2Models.Relays8);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == trace && t.IsError && t.Direction == BoardTraceDirection.Received && t.Bytes is { Length: > 0 }));

        Assert.False(run.Client.State.IsConnected);
        Assert.Null(run.Client.State.Status);
    }

    [Fact]
    public async Task Should_MoveToAnotherBoard_WhenTheAddressChanges()
    {
        await using var first = Started(DaenetIp2Models.Relays8);
        await using var second = Started(DaenetIp2Models.Relays8);
        await second.SwitchRelayAsync(3, true);
        await using var run = ClientRun.Start(first.Port, DaenetIp2Models.Relays8);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        run.Client.SetEndpoint(new BoardEndpoint("127.0.0.1", second.Port));

        await Wait.UntilAsync(() => run.Client.State.IsOn(3));
        Assert.Equal(second.Port, run.Client.Endpoint.Port);
        Assert.Contains(run.Changes, s => !s.IsConnected && s.Status is null);
    }

    [Fact]
    public async Task Should_NeverLogOrTraceTheCommunity()
    {
        await using var simulator = new DaenetIp2Simulator(DaenetIp2Models.Relays8, "s3cret!", NullLogger.Instance);
        simulator.Start();
        var logger = new ListLogger();
        await using var run = ClientRun.Start(simulator.Port, DaenetIp2Models.Relays8, community: "s3cret!", logger: logger);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        await run.Client.TurnOnAsync(0b0011, Cancel);
        run.Client.SetCommunity("s3cret?");
        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "No answer within 1 s: asking again") || simulator.UnansweredCount > 0);

        var secretHex = DaenetIp2Snmp.Hex("s3cret"u8);
        Assert.NotEmpty(run.Traces);
        Assert.All(run.Traces, t => Assert.DoesNotContain("s3cret", t.Text, StringComparison.Ordinal));
        Assert.All(run.Traces, t => Assert.DoesNotContain(secretHex, t.Bytes ?? "", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, m => m == "DAEnetIP2 v2 community set");
        Assert.All(logger.Messages, m => Assert.DoesNotContain("s3cret", m, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_RefuseCommands_BeforeTheBoardAnswered()
    {
        using var client = CreateClient(1, DaenetIp2Models.Relays8, new ManualTimeProvider());

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => client.TurnOnAsync(0b0001, Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => client.TurnOffAsync(0, Cancel));
        Assert.Throws<BoardNotConnectedException>(client.Refresh);
    }

    [Fact]
    public void Should_StartWithoutAnAddress()
    {
        using var client = CreateClient(DaenetIp2Api.DefaultPort, DaenetIp2Models.Relays16, TimeProvider.System, host: "");

        Assert.False(client.Endpoint.HasHost);
        Assert.Equal(161, client.Endpoint.Port);
    }

    [Fact]
    public void Should_RefuseSettingsAndModelsItCantUse()
    {
        Assert.Throws<ArgumentException>(() => CreateClient(1, DaenetIp2Models.Relays8, TimeProvider.System, pollSeconds: 0));
        Assert.Throws<ArgumentException>(() => CreateClient(1, DaenetIp2Models.Relays8, TimeProvider.System, community: "thirteen-char"));
        Assert.Throws<ArgumentException>(() => new DaenetIp2Client(
            Options.Create(new DaenetIp2Options()),
            Contactor.Boards.Velleman.Vm201Model.Model,
            TimeProvider.System,
            NullLogger<DaenetIp2Client>.Instance));
        using var client = CreateClient(1, DaenetIp2Models.Relays8, TimeProvider.System);
        Assert.Throws<ArgumentException>(() => client.SetCommunity("with space"));
        Assert.Equal(Community, client.Community);
    }

    // A command the test has moved the clock far enough for: done at once, or the test fails within the same 30 s as
    // Wait.UntilAsync, instead of waiting for a timeout that never comes.
    private static Task<T> Done<T>(Task<T> command)
    {
        return command.WaitAsync(TimeSpan.FromSeconds(30), Cancel);
    }

    // The client's GetRequest of a port with this request id, as its PDU.
    private static string Get(int id, DaenetIp2Port port)
    {
        return DaenetIp2Snmp.Hex(DaenetIp2Snmp.GetPort(id, Community, port).Pdu);
    }

    // The client's SetRequest of a pin with this request id, as its PDU.
    private static string Set(int id, DaenetIp2Port port, int pin, bool high)
    {
        return DaenetIp2Snmp.Hex(DaenetIp2Snmp.SetPin(id, Community, new DaenetIp2Pin(port, pin), high).Pdu);
    }

    // The board's answer to a GetRequest of a port, as its PDU.
    private static string Answer(int id, DaenetIp2Port port, int value)
    {
        return DaenetIp2Snmp.Hex(DaenetIp2Snmp.Decode(DaenetIp2Snmp.Response(id, Community, DaenetIp2Api.PortOid(port), value)).Pdu);
    }

    private static Datagram Reply(DaenetIp2Message request, int value)
    {
        return new Datagram(DaenetIp2Snmp.Response(request.RequestId, Community, request.Oid, value));
    }

    private static Datagram Echo(DaenetIp2Message request)
    {
        return Reply(request, request.Value ?? 0);
    }

    private static DaenetIp2Simulator Started(BoardModel model)
    {
        var simulator = new DaenetIp2Simulator(model);
        simulator.Start();
        return simulator;
    }

    private static DaenetIp2Client CreateClient(
        int port,
        BoardModel model,
        TimeProvider time,
        int pollSeconds = 2,
        string host = "127.0.0.1",
        string community = Community,
        ILogger<DaenetIp2Client>? logger = null)
    {
        var options = new DaenetIp2Options { Host = host, Port = port, PollSeconds = pollSeconds, Community = community };
        return new DaenetIp2Client(Options.Create(options), model, time, logger ?? NullLogger<DaenetIp2Client>.Instance);
    }

    /// <summary>A datagram a scripted board sends: from its own port, or from another one.</summary>
    private sealed record Datagram(byte[] Bytes, bool FromElsewhere = false);

    private sealed class ClientRun : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _task;
        private readonly List<BoardTraceEntry> _traces = [];
        private readonly List<DaenetIp2State> _changes = [];

        private ClientRun(DaenetIp2Client client, ManualTimeProvider time, TimeSpan pollInterval, CancellationTokenSource cts)
        {
            Client = client;
            Time = time;
            PollInterval = pollInterval;
            _cts = cts;
            client.Traced += (_, entry) => Add(_traces, entry);
            client.StateChanged += (_, state) => Add(_changes, state);
            _task = client.RunAsync(cts.Token);
        }

        public DaenetIp2Client Client { get; }

        /// <summary>Cancelled when the run ends: a command given it never outlives a test that failed.</summary>
        public CancellationToken Stopping => _cts.Token;

        public ManualTimeProvider Time { get; }

        /// <summary>How long the client waits between polls: the one timer a test waits for while the client is idle.</summary>
        public TimeSpan PollInterval { get; }

        public List<BoardTraceEntry> Traces => Copy(_traces);

        public List<DaenetIp2State> Changes => Copy(_changes);

        public static ClientRun Start(
            int port,
            BoardModel model,
            int pollSeconds = 2,
            string community = Community,
            ILogger<DaenetIp2Client>? logger = null,
            string host = "127.0.0.1")
        {
            var time = new ManualTimeProvider();
            var client = CreateClient(port, model, time, pollSeconds, host, community, logger);
            return new ClientRun(client, time, TimeSpan.FromSeconds(pollSeconds), new CancellationTokenSource());
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
    /// A board on a loopback UDP port that reads each SNMP request and sends what the test's function returns, in that
    /// order; nothing for an empty list. A datagram marked <see cref="Datagram.FromElsewhere"/> goes from a second port.
    /// </summary>
    private sealed class ScriptedBoard : IAsyncDisposable
    {
        private readonly Socket _socket;
        private readonly Socket _elsewhere;
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<DaenetIp2Message, IReadOnlyList<Datagram>> _answer;
        private readonly Task _loop;

        private ScriptedBoard(Socket socket, Socket elsewhere, Func<DaenetIp2Message, IReadOnlyList<Datagram>> answer)
        {
            _socket = socket;
            _elsewhere = elsewhere;
            _answer = answer;
            _loop = ServeAsync();
        }

        public int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;

        public int ElsewherePort => ((IPEndPoint)_elsewhere.LocalEndPoint!).Port;

        /// <param name="answer">What to send for each request.</param>
        /// <param name="elsewhere">
        /// Where <see cref="Datagram.FromElsewhere"/> comes from: this address and the board's own port, so it differs
        /// from the board in its address only; null: 127.0.0.1 and another port.
        /// </param>
        public static ScriptedBoard Start(Func<DaenetIp2Message, IReadOnlyList<Datagram>> answer, IPAddress? elsewhere = null)
        {
            var socket = Bound(IPAddress.Loopback, 0);
            return new ScriptedBoard(socket, Bound(elsewhere ?? IPAddress.Loopback, elsewhere is null ? 0 : ((IPEndPoint)socket.LocalEndPoint!).Port), answer);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _socket.Dispose();
            _elsewhere.Dispose();
            await _loop;
            _stop.Dispose();
        }

        private static Socket Bound(IPAddress address, int port)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(address, port));
            return socket;
        }

        private async Task ServeAsync()
        {
            var buffer = new byte[DaenetIp2Api.MaxMessageBytes];
            try
            {
                while (true)
                {
                    var received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _stop.Token);
                    var request = DaenetIp2Snmp.Decode(buffer.AsSpan(0, received.ReceivedBytes));
                    foreach (var datagram in _answer(request))
                    {
                        var from = datagram.FromElsewhere ? _elsewhere : _socket;
                        await from.SendToAsync(datagram.Bytes, SocketFlags.None, received.RemoteEndPoint, _stop.Token);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // The test ended.
            }
        }
    }

    private sealed class ListLogger : ILogger<DaenetIp2Client>
    {
        private readonly List<string> _messages = [];

        public List<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return [.. _messages];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_messages)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }

    private static bool CanBindUdp(IPAddress address)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(address, 0));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
