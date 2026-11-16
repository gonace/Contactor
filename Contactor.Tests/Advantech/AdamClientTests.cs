using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Contactor.Boards;
using Contactor.Boards.Advantech;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Advantech;

/// <summary>
/// The client against the simulator, and against a scripted module for answers the simulator never gives. Its clock is
/// a <see cref="ManualTimeProvider"/>: a poll, a wait or a timeout only comes when the test moves the clock.
/// </summary>
public sealed class AdamClientTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ReportTheRelaysAndInputs_WithRelaySixAsItsOwnBit()
    {
        await using var simulator = Started();
        await simulator.SwitchRelayAsync(2, true);
        await simulator.SwitchRelayAsync(6, true);
        await simulator.SetInputAsync(3, true);
        await using var run = ClientRun.Start(simulator.Port);

        // Connected, and the first answer reported: the client waits for the next poll.
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        Assert.Equal(new AdamStatus((1UL << 5) | 0b10, 0b100), run.Client.State.Status);
        Assert.True(run.Client.State.IsOn(6));
        Assert.Equal(run.Time.GetUtcNow(), run.Client.State.LastContactUtc);
        Assert.Equal([ReadRelays(1), ReadInputs(2)], simulator.ReceivedRequests);
        Assert.Contains(run.Changes, s => s.IsConnected && s.Relays == ((1UL << 5) | 0b10) && s.Inputs == 0b100);
        Assert.Contains(run.Traces, t => t.Text == "Relays: 2 and 6 on" && t.Direction == BoardTraceDirection.Received);
        Assert.Contains(run.Traces, t => t.Text == "Inputs: 3 on" && t.Direction == BoardTraceDirection.Received);
    }

    [Fact]
    public async Task Should_ShowAnInputSetAtTheModule_OnTheNextPoll()
    {
        await using var simulator = Started();
        await using var run = ClientRun.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        await simulator.SetInputAsync(6, true);
        await run.PollAsync();

        Assert.Equal(1UL << 5, run.Client.State.Inputs);
        Assert.Contains(run.Changes, s => s.Inputs == 1UL << 5);
    }

    [Fact]
    public async Task Should_ReadOnlyTheRelays_OnAModuleWithoutInputs()
    {
        // The ADAM-6260: six relays at the same coils, and no inputs, so no FC 02.
        await using var simulator = new AdamSimulator(AdamModels.Adam6260);
        simulator.Start();
        await simulator.SwitchRelayAsync(2, true);
        await using var run = ClientRun.Start(simulator.Port, model: AdamModels.Adam6260);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        Assert.Equal(new AdamStatus(0b10, 0), run.Client.State.Status);
        Assert.Equal(1UL << 5, await run.Client.TurnOnAsync(1UL << 5, Cancel));

        Assert.Equal((1UL << 5) | 0b10, run.Client.State.Relays);
        Assert.Equal([ReadRelays(1), "00 02 00 00 00 06 01 05 00 15 FF 00", ReadRelays(3)], simulator.ReceivedRequests);
        Assert.DoesNotContain(run.Traces, t => t.Text.StartsWith("Inputs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_SwitchOneRelayPerRequest_AndReadTheStateAfter()
    {
        await using var simulator = Started();
        await using var run = ClientRun.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal((1UL << 5) | 1UL, await run.Client.TurnOnAsync((1UL << 5) | 1UL, Cancel));

        // Confirmed by the read in the command itself: no time had to pass for a poll.
        Assert.Equal((1UL << 5) | 1UL, simulator.Relays);
        Assert.Equal((1UL << 5) | 1UL, run.Client.State.Relays);

        Assert.Equal(1UL, await run.Client.TurnOffAsync(1UL, Cancel));
        Assert.Equal(1UL << 5, run.Client.State.Relays);
        Assert.Equal(
            [
                ReadRelays(1),
                ReadInputs(2),
                "00 03 00 00 00 06 01 05 00 10 FF 00",
                "00 04 00 00 00 06 01 05 00 15 FF 00",
                ReadRelays(5),
                ReadInputs(6),
                "00 07 00 00 00 06 01 05 00 10 00 00",
                ReadRelays(8),
                ReadInputs(9),
            ],
            simulator.ReceivedRequests);
        Assert.Contains(run.Traces, t => t.Text == "Turn on relay 6" && t.Direction == BoardTraceDirection.Sent && !t.IsRoutine);
        Assert.Contains(run.Traces, t => t.Text == "Relay 6 turned on" && t.Direction == BoardTraceDirection.Received);
        Assert.Equal(1, simulator.ConnectionCount);
    }

    [Fact]
    public async Task Should_RefuseRelaysTheModuleHasNot_AndSendNothingForNone()
    {
        await using var simulator = Started();
        await using var run = ClientRun.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.TurnOnAsync(1UL << 6, Cancel));
        Assert.Equal(0UL, await run.Client.TurnOffAsync(0, Cancel));
        Assert.Equal(0UL, await run.Client.TurnOnAsync(0, Cancel));

        Assert.Equal([ReadRelays(1), ReadInputs(2)], simulator.ReceivedRequests);
    }

    [Fact]
    public async Task Should_SendTheWholeMask_WhenTheModuleRefusesOneRelay()
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
        Assert.Contains(run.Traces, t => t.Text == "Refused: it failed (code 4)" && t.IsError && t.Bytes == "00 04 00 00 00 03 01 85 04");
        Assert.Contains(run.Traces, t => t.Text.StartsWith("The module didn't take the command for relay 2: ", StringComparison.Ordinal) && t.IsError);
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_SendTheRestOfTheMask_OnANewConnection_AfterAnUnreadableAnswer()
    {
        // Relay 2's echo carries another transaction id: the connection may be out of step, so relay 3 goes on a new one.
        await using var module = ScriptedModule.Start(request => request[7] switch
        {
            AdamApi.WriteSingleCoil when request[9] == 0x11 => [0xEE, 0xEE, .. request[2..]],
            AdamApi.WriteSingleCoil => request,
            _ => Answer(request, 0b0101),
        });
        await using var run = ClientRun.Start(module.Port, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal(0b0101UL, await run.Client.TurnOnAsync(0b0111, Cancel));

        Assert.Equal(0b0101UL, run.Client.State.Relays);
        Assert.Contains(run.Traces, t => t.Text.StartsWith("Unreadable answer: The answer is for transaction 61166", StringComparison.Ordinal) && t.IsError);
        Assert.Contains(run.Traces, t => t.Text == "Relay 3 turned on");
        Assert.Equal(2, module.Connections);
    }

    [Fact]
    public async Task Should_KeepTheStateItRead_AndReadAgainAtOnce_WhenTheReadAfterASwitchIsUnreadable()
    {
        // The module echoes the ON but the relay stays off, and the read in the command is unreadable.
        var reads = 0;
        await using var module = ScriptedModule.Start(request =>
        {
            if (request[7] != AdamApi.ReadCoils)
            {
                return request[7] == AdamApi.WriteSingleCoil ? request : Answer(request, 0);
            }

            // The second read of the relays carries two bytes of relays, not one.
            return Interlocked.Increment(ref reads) == 2
                ? AdamModbus.Frame(BinaryPrimitives.ReadUInt16BigEndian(request), request[6], [AdamApi.ReadCoils, 2, 0, 0])
                : Answer(request, 0);
        });
        await using var run = ClientRun.Start(module.Port, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        Assert.Equal(1UL, await run.Client.TurnOnAsync(0b0001, Cancel));

        // The read at once, not in a minute, shows the truth: still off.
        await Wait.UntilAsync(() => Volatile.Read(ref reads) == 3 && run.Time.WaitsOnly(run.PollInterval));
        Assert.DoesNotContain(run.Changes, s => s.IsOn(1));
        Assert.False(run.Client.State.IsOn(1));
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_ThrowNotConnected_NotRefused_WhenTheModuleRefusesEveryRelay()
    {
        await using var simulator = Started();
        simulator.FailRelays = 0b0011;
        await using var run = ClientRun.Start(simulator.Port, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        var error = await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0011, Cancel));

        // A read at once, not in a minute, and the module still counts as there: it answered.
        Assert.Equal("The ADAM module didn't take the command: The module refused it: it failed (code 4).", error.Message);
        Assert.IsType<BoardRefusedException>(error.InnerException);
        await Wait.UntilAsync(() => simulator.ReceivedRequests.Count(r => r.EndsWith("01 01 00 10 00 06", StringComparison.Ordinal)) == 2);
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_LoseTheModule_AfterThreeUnansweredPolls_AndConnectAgain()
    {
        await using var simulator = Started();
        await using var run = ClientRun.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        var noAnswer = $"The ADAM module did not answer within {AdamClient.RequestTimeout.TotalSeconds} s.";

        simulator.NoAnswer = true;
        for (var poll = 1; poll <= BoardPoller<AdamStatus>.DefaultFailuresBeforeLost; poll++)
        {
            Assert.True(run.Client.State.IsConnected);
            var failures = poll;
            await Wait.UntilAsync(() => run.Time.WaitsOnly(run.PollInterval) && simulator.UnansweredCount == failures - 1);
            run.Time.Advance(run.PollInterval);
            await Wait.UntilAsync(() => simulator.UnansweredCount == failures && run.Time.WaitsOnly(AdamClient.RequestTimeout));
            run.Time.Advance(AdamClient.RequestTimeout);
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
        await Wait.UntilAsync(() => simulator.UnansweredCount == 1 && run.Time.WaitsOnly(AdamClient.RequestTimeout));
        run.Time.Advance(AdamClient.RequestTimeout);
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
    public async Task Should_CarryOn_OnANewConnection_WhenTheModuleDropsIt()
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
        // The command takes the poll's place on the connection it has, and the poll's answer never comes.
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
        await Wait.UntilAsync(() => simulator.UnansweredCount == 1 && run.Time.WaitsOnly(AdamClient.RequestTimeout));
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
        await using var module = ScriptedModule.Start(request =>
        {
            if (request[7] == AdamApi.WriteSingleCoil)
            {
                byte[] answer = [.. Interlocked.Exchange(ref held, null) ?? [], .. request];
                return answer;
            }

            if (Volatile.Read(ref holdReads) == 1)
            {
                Volatile.Write(ref held, Answer(request, 0b10));
                return null;
            }

            return Answer(request, 0);
        });
        await using var run = ClientRun.Start(module.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        Volatile.Write(ref holdReads, 1);
        run.Time.Advance(run.PollInterval);
        await Wait.UntilAsync(() => module.Requests == 3 && run.Time.WaitsOnly(AdamClient.RequestTimeout));
        Volatile.Write(ref holdReads, 0);

        Assert.Equal(1UL, await run.Client.TurnOnAsync(1, Cancel));

        Assert.Equal(1, module.Connections);
        Assert.Contains(run.Traces, t => t.Text == "Skipped a late answer nobody waited for" && t.Bytes == "00 03 00 00 00 04 01 01 01 02");
        Assert.True(run.Client.State.IsConnected);
        Assert.DoesNotContain(run.Changes, s => s.IsOn(2));
    }

    [Fact]
    public async Task Should_SendEveryOff_OnANewConnection_WhenTheModuleClosedTheOldOne()
    {
        await using var simulator = Started();
        await using var run = ClientRun.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        await simulator.SwitchRelayAsync(1, true);
        await simulator.SwitchRelayAsync(2, true);
        await simulator.SwitchRelayAsync(3, true);

        // The module closes the connection, e.g. as idle; "All off" comes before the next poll.
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
        // The switches are answered; the read of the relays after them gets no answer.
        var reads = 0;
        await using var module = ScriptedModule.Start(request => request[7] switch
        {
            AdamApi.WriteSingleCoil => request,
            AdamApi.ReadCoils when Interlocked.Increment(ref reads) == 2 => null,
            _ => Answer(request, 1),
        });
        await using var run = ClientRun.Start(module.Port, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        var sent = run.Client.TurnOnAsync(1, Cancel);
        await Wait.UntilAsync(() => Volatile.Read(ref reads) == 2 && run.Time.PendingTimers == 2);
        run.Time.Advance(AdamClient.RequestTimeout);

        Assert.Equal(1UL, await sent);
        await Wait.UntilAsync(() => Volatile.Read(ref reads) == 3 && run.Time.WaitsOnly(run.PollInterval));
        Assert.True(run.Client.State.IsOn(1));
        Assert.True(run.Client.State.IsConnected);
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
        run.Time.Advance(AdamClient.RequestTimeout);
        var error = await Assert.ThrowsAsync<BoardNotConnectedException>(() => sent);

        // Relay 2 never went: no answer stops the rest.
        Assert.Equal("The ADAM module didn't answer; the command may not have been carried out.", error.Message);
        Assert.DoesNotContain(simulator.ReceivedRequests, r => r.EndsWith("01 05 00 11 FF 00", StringComparison.Ordinal));
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
    [InlineData("00 02 00 00 00 04 01 01 01 00", "Unreadable answer: The answer is for transaction 2, not 1.")]
    [InlineData("00 01 00 00 FF FF 01 01", "Unreadable answer: The answer's length 65535 isn't a Modbus length.")]
    [InlineData("00 01 00 00 00 05 01 01 01 00 00", "Unreadable answer: The answer is 5 bytes long, not 4.")]
    [InlineData("00 01 00 00 00 04 01 01 02 00", "Unreadable answer: The answer has 2 bytes of data, not 1.")]
    [InlineData("00 01 00 00 00 04 00 01 01 00", "Unreadable answer: The answer came from unit 0, not 1.")]
    [InlineData("00 01 00 00 00 03 01 81 02", "Refused: no such address (code 2)")]
    public async Task Should_NeverConnect_WhenThePollsAnswerIsntOne(string answer, string trace)
    {
        await using var module = ScriptedModule.Start(_ => AdamModbusTests.Bytes(answer));
        await using var run = ClientRun.Start(module.Port);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == trace && t.IsError && t.Direction == BoardTraceDirection.Received && t.Bytes is { Length: > 0 }));

        Assert.False(run.Client.State.IsConnected);
        Assert.Null(run.Client.State.Status);
    }

    [Fact]
    public async Task Should_NeverConnect_WhenTheInputsAnswerIsntOne()
    {
        // The relays are answered; the inputs' answer is for function 01.
        await using var module = ScriptedModule.Start(request => request[7] == AdamApi.ReadCoils
            ? Answer(request, 1)
            : AdamModbus.Frame(BinaryPrimitives.ReadUInt16BigEndian(request), request[6], [AdamApi.ReadCoils, 1, 0]));
        await using var run = ClientRun.Start(module.Port);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "Unreadable answer: The answer is for function 01, not 02." && t.IsError));

        Assert.False(run.Client.State.IsConnected);
        Assert.Null(run.Client.State.Status);
    }

    [Fact]
    public async Task Should_SkipBytesNobodyAskedFor_BeforeTheNextRequest()
    {
        await using var module = ScriptedModule.Start(request => [.. Answer(request, 1), 0xAA, 0xBB]);
        await using var run = ClientRun.Start(module.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        run.Time.Advance(run.PollInterval);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "Skipped 2 bytes nobody asked for" && t.Bytes == "AA BB"));
        await Wait.UntilAsync(() => module.Requests == 4 && run.Time.WaitsOnly(run.PollInterval));
        Assert.True(run.Client.State.IsOn(1));
    }

    [Fact]
    public async Task Should_MoveToAnotherModule_WhenTheAddressChanges()
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
        using var client = CreateClient(AdamApi.DefaultPort, TimeProvider.System, host: "");

        Assert.False(client.Endpoint.HasHost);
        Assert.Equal(502, client.Endpoint.Port);
    }

    [Fact]
    public void Should_RefuseSettingsAndModelsItCantUse()
    {
        Assert.Throws<ArgumentException>(() => CreateClient(1, TimeProvider.System, pollSeconds: 0));
        Assert.Throws<ArgumentException>(() => new AdamClient(
            Options.Create(new AdamOptions()),
            Contactor.Boards.Velleman.Vm201Model.Model,
            TimeProvider.System,
            NullLogger<AdamClient>.Instance));
    }

    // The client's poll of the relays with this transaction id.
    private static string ReadRelays(int transaction)
    {
        return AdamModbus.Hex(AdamModbus.ReadRelays((ushort)transaction, 6).Frame);
    }

    // The client's poll of the inputs with this transaction id.
    private static string ReadInputs(int transaction)
    {
        return AdamModbus.Hex(AdamModbus.ReadInputs((ushort)transaction, 6).Frame);
    }

    // An answer to a read of the 6 relays (FC 01) or the 6 inputs (FC 02): one byte of bits.
    private static byte[] Answer(byte[] request, byte relays, byte inputs = 0)
    {
        var transaction = BinaryPrimitives.ReadUInt16BigEndian(request);
        var function = request[7];
        return AdamModbus.Frame(transaction, request[6], [function, 1, function == AdamApi.ReadDiscreteInputs ? inputs : relays]);
    }

    private static AdamSimulator Started()
    {
        var simulator = new AdamSimulator(AdamModels.Adam6060);
        simulator.Start();
        return simulator;
    }

    private static AdamClient CreateClient(int port, TimeProvider time, int pollSeconds = 2, string host = "127.0.0.1", BoardModel? model = null)
    {
        var options = new AdamOptions { Host = host, Port = port, PollSeconds = pollSeconds };
        return new AdamClient(Options.Create(options), model ?? AdamModels.Adam6060, time, NullLogger<AdamClient>.Instance);
    }

    private sealed class ClientRun : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _task;
        private readonly List<BoardTraceEntry> _traces = [];
        private readonly List<AdamState> _changes = [];

        private ClientRun(AdamClient client, ManualTimeProvider time, TimeSpan pollInterval, CancellationTokenSource cts)
        {
            Client = client;
            Time = time;
            PollInterval = pollInterval;
            _cts = cts;
            client.Traced += (_, entry) => Add(_traces, entry);
            client.StateChanged += (_, state) => Add(_changes, state);
            _task = client.RunAsync(cts.Token);
        }

        public AdamClient Client { get; }

        public ManualTimeProvider Time { get; }

        /// <summary>How long the client waits between polls: the one timer a test waits for while the client is idle.</summary>
        public TimeSpan PollInterval { get; }

        public List<BoardTraceEntry> Traces => Copy(_traces);

        public List<AdamState> Changes => Copy(_changes);

        public static ClientRun Start(int port, int pollSeconds = 2, BoardModel? model = null)
        {
            var time = new ManualTimeProvider();
            return new ClientRun(CreateClient(port, time, pollSeconds, model: model), time, TimeSpan.FromSeconds(pollSeconds), new CancellationTokenSource());
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
    /// A module that reads each Modbus TCP request by its header and answers it with what the test's function returns;
    /// null answers nothing.
    /// </summary>
    private sealed class ScriptedModule : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<byte[], byte[]?> _answer;
        private readonly Task _loop;
        private int _requests;
        private int _connections;

        private ScriptedModule(TcpListener listener, Func<byte[], byte[]?> answer)
        {
            _listener = listener;
            _answer = answer;
            _loop = ServeAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Requests => Volatile.Read(ref _requests);

        public int Connections => Volatile.Read(ref _connections);

        public static ScriptedModule Start(Func<byte[], byte[]?> answer)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new ScriptedModule(listener, answer);
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
                        var head = new byte[AdamApi.HeaderBytes];
                        await stream.ReadExactlyAsync(head, _stop.Token);
                        var request = new byte[AdamApi.HeaderBytes - 1 + BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4))];
                        head.CopyTo(request, 0);
                        await stream.ReadExactlyAsync(request.AsMemory(AdamApi.HeaderBytes), _stop.Token);
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
