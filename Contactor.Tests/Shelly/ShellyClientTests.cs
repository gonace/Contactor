using System.Net;
using System.Net.Sockets;
using System.Text;
using Contactor.Boards;
using Contactor.Boards.Shelly;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Shelly;

/// <summary>
/// The client against the simulator, and against a fake web server for answers the simulator never gives. Each runs on
/// a <see cref="ManualTimeProvider"/>: a poll, a wait or a timeout only comes when the test moves the clock.
/// </summary>
public sealed class ShellyClientTests
{
    private const string Password = "s3cret ö-Pw";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ConnectAndReportStatus_WithoutAPassword()
    {
        await using var simulator = Start(ShellyModels.FourRelays);
        await simulator.SwitchRelayAsync(2, true);
        await using var run = ClientRun.Start(simulator, ShellyModels.FourRelays);

        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal(new ShellyStatus(0b0010, AllOff(4), OwnRules.Empty), run.Client.State.Status);
        Assert.True(run.Client.State.IsOn(2));
        Assert.Null(run.Client.State.Problem);

        // Each switch's settings and the schedules once at connect, after the status: not with the first poll.
        Assert.Equal(
            [ShellyApi.GetDeviceInfo, ShellyApi.GetStatus, .. Enumerable.Repeat(ShellyApi.SwitchGetConfig, 4), ShellyApi.ScheduleList, ShellyApi.GetStatus],
            simulator.Received);
        Assert.Contains(run.Traces, t => t.Text == "Device SIM-4, generation 2, firmware 2.0.0");
        Assert.Contains(run.Traces, t => t.Text == "Status: relay 2 on" && t.Direction == BoardTraceDirection.Received);
    }

    [Fact]
    public async Task Should_SwitchOneRelayPerSwitchSet_AndReadAtOnceAfterwards()
    {
        await using var simulator = Start(ShellyModels.FourRelays);
        await using var run = ClientRun.Start(simulator, ShellyModels.FourRelays, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal(0b0101, await run.Client.TurnOnAsync(0b0101, Cancel));
        Assert.Equal(0b0101, simulator.Relays);
        Assert.Equal(0b0101, run.Client.State.Relays);

        Assert.Equal(0b0001, await run.Client.TurnOffAsync(0b0001, Cancel));
        Assert.Equal(0b0100, run.Client.State.Relays);

        // Only Switch.Set, one per relay, each followed by nothing else until the read after the command.
        Assert.Equal(
            [ShellyApi.SwitchSet, ShellyApi.SwitchSet, ShellyApi.GetStatus, ShellyApi.SwitchSet, ShellyApi.GetStatus],
            simulator.Received.Skip(8));
        Assert.Contains(run.Traces, t => t.Text == "Turn on relay 3" && t.Direction == BoardTraceDirection.Sent);
        Assert.Contains(run.Traces, t => t.Text == "Relay 3 is on (it was off)");
        Assert.Equal(0, await run.Client.TurnOnAsync(0, Cancel));
    }

    [Fact]
    public async Task Should_ReuseTheNonce_OnFirmware2()
    {
        await using var simulator = Start(ShellyModels.TwoRelays, Password);
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays, Password);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        for (var poll = 0; poll < 3; poll++)
        {
            await run.PollAsync();
        }

        await run.Client.TurnOnAsync(0b0011, Cancel);

        // Device info needs no password; the first status gets the one challenge, and every call after it goes signed
        // at once.
        Assert.Equal(1, simulator.ChallengeCount);
        Assert.Equal(0, simulator.DeniedCount);
        Assert.Equal(
            [ShellyApi.GetDeviceInfo, ShellyApi.GetStatus, $"{ShellyApi.GetStatus} (signed)"],
            simulator.Received.Take(3));
        Assert.All(simulator.Received.Skip(3), r => Assert.EndsWith(" (signed)", r, StringComparison.Ordinal));
        Assert.Equal(0b0011, simulator.Relays);
    }

    [Fact]
    public async Task Should_AnswerAChallengeForEveryRequest_OnFirmware17()
    {
        await using var simulator = Start(ShellyModels.TwoRelays, Password, firmware: "1.7.1");
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays, Password);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        await run.PollAsync();

        await run.Client.TurnOnAsync(0b0010, Cancel);

        // Status, two Switch.GetConfig and Schedule.List at connect, the first poll, a poll, the Switch.Set and the read
        // after it: eight challenges, each answered.
        Assert.Equal(8, simulator.ChallengeCount);
        Assert.Equal(0, simulator.DeniedCount);
        Assert.Equal(
            [ShellyApi.GetDeviceInfo, ShellyApi.GetStatus, $"{ShellyApi.GetStatus} (signed)", ShellyApi.SwitchGetConfig, $"{ShellyApi.SwitchGetConfig} (signed)"],
            simulator.Received.Take(5));
        Assert.Equal(0b0010, simulator.Relays);
        Assert.True(run.Client.State.IsConnected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_TakeANewNonceOnce_WhenTheDeviceRefusesTheKeptOne(bool stale)
    {
        await using var simulator = Start(ShellyModels.OneRelay, Password);
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, Password);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        var challenges = simulator.ChallengeCount;

        if (stale)
        {
            simulator.ExpireNonces();
        }
        else
        {
            simulator.ForgetNonces();
        }

        await run.PollAsync();
        await run.PollAsync();

        // One refusal, its new challenge answered at once, and the new nonce kept for the poll after.
        Assert.Equal(challenges + 1, simulator.ChallengeCount);
        Assert.Equal(stale ? 1 : 0, simulator.StaleCount);
        Assert.Equal(0, simulator.DeniedCount);
        Assert.Contains(run.Traces, t => t.Text == "The Shelly wants a new nonce");
        Assert.DoesNotContain(run.Traces, t => t.IsError);
        Assert.True(run.Client.State.IsConnected);
    }

    // A power cut is a reboot: the device forgot its nonces, so the kept one gets a plain 401 and the app takes a new one.
    [Fact]
    public async Task Should_TakeANewNonce_AfterAPowerCut()
    {
        await using var simulator = Start(ShellyModels.OneRelay, Password);
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, Password);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        var challenges = simulator.ChallengeCount;

        await simulator.CutPowerAsync();
        await simulator.RestorePowerAsync();
        await run.PollAsync();
        await run.PollAsync();

        Assert.Equal(challenges + 1, simulator.ChallengeCount);
        Assert.Equal(0, simulator.StaleCount);
        Assert.Equal(0, simulator.DeniedCount);
        Assert.True(run.Client.State.IsConnected);
    }

    // The password changed on the device while the app runs: the kept nonce is refused, the new challenge is answered
    // once, and that is refused too. Nothing more in that request, and no kept nonce for the next.
    [Fact]
    public async Task Should_StopAfterOneNewChallenge_WhenTheKeptNonceAndThenThePasswordAreRefused()
    {
        await using var simulator = Start(ShellyModels.OneRelay, Password);
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, Password);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        var challenges = simulator.ChallengeCount;
        var sent = simulator.Received.Count;
        simulator.ChangePassword("changed");

        run.Time.Advance(run.PollInterval);
        await Wait.UntilAsync(() => simulator.DeniedCount == 2 && run.Time.WaitsOnly(run.PollInterval));

        // The kept nonce (wrong, as the password is) and one answer to the new challenge: two requests, two refusals.
        Assert.Equal([$"{ShellyApi.GetStatus} (signed)", $"{ShellyApi.GetStatus} (signed)"], simulator.Received.Skip(sent));
        Assert.Equal(challenges + 2, simulator.ChallengeCount);
        Assert.Contains(run.Traces, t => t.Text == "The Shelly wants a new nonce");
        Assert.Contains(run.Traces, t => t.Text == "Wrong password" && t.IsError);
        Assert.True(run.Client.State.IsConnected);

        // The next poll has no kept nonce: it asks without the password first, then answers the challenge once.
        sent = simulator.Received.Count;
        run.Time.Advance(run.PollInterval);
        await Wait.UntilAsync(() => simulator.DeniedCount == 3 && run.Time.WaitsOnly(run.PollInterval));
        Assert.Equal([ShellyApi.GetStatus, $"{ShellyApi.GetStatus} (signed)"], simulator.Received.Skip(sent));
    }

    // "A single nonce can be reused for up to 30,000 requests": the client asks for a new one when it reaches the limit,
    // without waiting for the device to refuse it. The limit is lowered here, so the test doesn't send 30,000 requests.
    [Fact]
    public async Task Should_TakeANewNonce_WhenItUsedTheKeptOneForTheMostRequests()
    {
        await using var simulator = Start(ShellyModels.OneRelay, Password);
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, Password);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        Assert.Equal(ShellyClient.MaxNonceCount, run.Client.NonceLimit);
        run.Client.NonceLimit = 2;
        await run.PollAsync();
        var challenges = simulator.ChallengeCount;
        var sent = simulator.Received.Count;

        // The poll above took a new nonce (the old one was past the limit), its first request. The next poll uses it a
        // second time; the one after goes without it, gets a new challenge and answers it.
        await run.PollAsync();
        await run.PollAsync();

        Assert.Equal([$"{ShellyApi.GetStatus} (signed)", ShellyApi.GetStatus, $"{ShellyApi.GetStatus} (signed)"], simulator.Received.Skip(sent));
        Assert.Equal(challenges + 1, simulator.ChallengeCount);
        Assert.Equal(0, simulator.DeniedCount);
        Assert.Equal(0, simulator.StaleCount);
        Assert.DoesNotContain(run.Traces, t => t.Text == "The Shelly wants a new nonce");
    }

    // Issue #522: each try counts toward the device's brute-force protection, so a refused password isn't tried again
    // by itself, however long the clock runs.
    [Fact]
    public async Task Should_StayDisconnected_AndNotTryARefusedPasswordAgain()
    {
        await using var simulator = Start(ShellyModels.OneRelay, Password);
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, "wrong");

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "Not trying again until a new password is saved or a person checks again"));
        Assert.Equal(1, simulator.DeniedCount);
        Assert.Equal(0, run.Time.PendingTimers);
        var sent = simulator.Received.Count;

        run.Time.Advance(TimeSpan.FromDays(1));
        await Task.Yield();

        Assert.Equal(1, simulator.DeniedCount);
        Assert.Equal(sent, simulator.Received.Count);
        Assert.False(run.Client.State.IsConnected);
        Assert.Equal(new ConnectionProblem(ConnectionProblemKind.LoginRefused, ShellyModels.OneRelay), run.Client.State.Problem);
        Assert.Single(run.Changes, c => c.Problem is not null);
        Assert.Contains(run.Traces, t => t.Text == "Wrong password" && t.IsError);
        Assert.DoesNotContain(run.Traces, t => t.Text.StartsWith("Trying again in", StringComparison.Ordinal));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0001, Cancel));

        // A new password connects at once, and clears the problem.
        run.Client.SetPassword(Password);
        Assert.Null(run.Client.State.Problem);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        Assert.Equal(1, simulator.DeniedCount);
    }

    // "Check again" tries the same password once, and holds again when the device refuses it again.
    [Fact]
    public async Task Should_TryOnce_WhenAPersonChecksAgain_AfterARefusedPassword()
    {
        await using var simulator = Start(ShellyModels.OneRelay, Password);
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, "wrong");
        await Wait.UntilAsync(() => simulator.DeniedCount == 1 && HeldCount(run) == 1);

        run.Client.TryAgain();
        Assert.Null(run.Client.State.Problem);
        await Wait.UntilAsync(() => simulator.DeniedCount == 2 && HeldCount(run) == 2);
        Assert.Equal(new ConnectionProblem(ConnectionProblemKind.LoginRefused, ShellyModels.OneRelay), run.Client.State.Problem);

        run.Time.Advance(TimeSpan.FromHours(1));
        await Task.Yield();
        Assert.Equal(2, simulator.DeniedCount);

        run.Client.SetPassword(Password);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        Assert.Null(run.Client.State.Problem);
        Assert.Equal(2, simulator.DeniedCount);

        // Connected: "Check again" leaves the connection as it is.
        var connects = run.Traces.Count(t => t.Text.StartsWith("Connecting to", StringComparison.Ordinal));
        run.Client.TryAgain();
        Assert.True(run.Client.State.IsConnected);
        Assert.Equal(connects, run.Traces.Count(t => t.Text.StartsWith("Connecting to", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Should_SayToWait_WhenTheDeviceAsksToWait_AfterARefusedPassword()
    {
        await using var simulator = Start(ShellyModels.OneRelay, Password);
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, "wrong");
        await Wait.UntilAsync(() => simulator.DeniedCount == 1 && HeldCount(run) == 1);
        var refused = new ConnectionProblem(ConnectionProblemKind.LoginRefused, ShellyModels.OneRelay);
        Assert.Equal(refused, run.Client.State.Problem);

        // The device's brute-force protection answers 429 now: the screens say to wait, not why.
        simulator.Throttle = true;
        run.Client.TryAgain();
        await Wait.UntilAsync(() => simulator.ThrottledCount == 1 && run.Time.WaitsOnly(BoardPoller<ShellyStatus>.AskedToWaitPause));

        var tooMany = new ConnectionProblem(ConnectionProblemKind.AskedToWait, ShellyModels.OneRelay);
        Assert.Equal(tooMany, run.Client.State.Problem);
        var changes = run.Changes;
        Assert.Equal([refused, tooMany], changes.Select(c => c.Problem).OfType<ConnectionProblem>().Distinct());
    }

    [Fact]
    public async Task Should_SendNothingMore_WhenTheAddressMovesDuringACommand()
    {
        await using var first = Start(ShellyModels.TwoRelays);
        await using var second = Start(ShellyModels.TwoRelays);
        await using var run = ClientRun.Start(first, ShellyModels.TwoRelays, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        // While the first relay's Switch.Set reaches the first device, the address moves to the second.
        first.Traced += (_, entry) =>
        {
            if (entry.Text == "Turn on relay 1")
            {
                run.Client.SetEndpoint(new BoardEndpoint("127.0.0.1", second.Port));
            }
        };

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0011, Cancel));

        Assert.Equal(0b0001, first.Relays);
        Assert.Equal(0, second.Relays);
        Assert.DoesNotContain(ShellyApi.SwitchSet, second.Received);
    }

    [Fact]
    public async Task Should_SayAPasswordIsNeeded_WhenTheAppHasNone()
    {
        await using var simulator = Start(ShellyModels.OneRelay, Password);
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay);

        await Wait.UntilAsync(() => run.Client.State.Problem is not null);

        Assert.Equal(new ConnectionProblem(ConnectionProblemKind.LoginNeeded, ShellyModels.OneRelay), run.Client.State.Problem);
        Assert.Equal(0, simulator.DeniedCount);
        Assert.Contains(run.Traces, t => t.Text == "The Shelly asks for a password. Enter it on the Settings screen." && t.IsError);
    }

    [Fact]
    public async Task Should_SayToWait_AndNeverRetryWithinARequest_OnA429()
    {
        await using var simulator = Start(ShellyModels.OneRelay, Password);
        simulator.Throttle = true;
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, Password);

        // Issue #522: not in the poller's quick loop, but after a wait longer than the device counts wrong passwords for.
        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "Trying again in 600 s") && run.Time.WaitsOnly(BoardPoller<ShellyStatus>.AskedToWaitPause));

        Assert.Equal(1, simulator.ThrottledCount);
        Assert.Equal([ShellyApi.GetDeviceInfo, ShellyApi.GetStatus], simulator.Received);
        Assert.False(run.Client.State.IsConnected);
        Assert.Equal(new ConnectionProblem(ConnectionProblemKind.AskedToWait, ShellyModels.OneRelay), run.Client.State.Problem);
        Assert.Contains(run.Traces, t => t.Text.StartsWith("The Shelly asks to wait", StringComparison.Ordinal) && t.IsError);
        Assert.Contains(run.Traces, t => t.Text.StartsWith("Connection lost: The Shelly asks to wait", StringComparison.Ordinal));

        // Nothing more until the wait runs out.
        run.Time.Advance(BoardPoller<ShellyStatus>.AskedToWaitPause - TimeSpan.FromSeconds(1));
        await Task.Yield();
        Assert.Equal(1, simulator.ThrottledCount);
        Assert.Equal(2, simulator.Received.Count);

        simulator.Throttle = false;
        run.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
    }

    // "Check again" doesn't wait for the 10 minutes: one attempt at once.
    [Fact]
    public async Task Should_TryOnceAtOnce_WhenAPersonChecksAgain_AfterA429()
    {
        await using var simulator = Start(ShellyModels.OneRelay, Password);
        simulator.Throttle = true;
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, Password);
        await Wait.UntilAsync(() => simulator.ThrottledCount == 1 && run.Time.WaitsOnly(BoardPoller<ShellyStatus>.AskedToWaitPause));

        run.Client.TryAgain();
        await Wait.UntilAsync(() => simulator.ThrottledCount == 2 && run.Time.WaitsOnly(BoardPoller<ShellyStatus>.AskedToWaitPause));

        simulator.Throttle = false;
        run.Client.TryAgain();
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        Assert.Equal(2, simulator.ThrottledCount);
    }

    private static int HeldCount(ClientRun run)
    {
        return run.Traces.Count(t => t.Text == "Not trying again until a new password is saved or a person checks again");
    }

    [Theory]
    [InlineData(null, "2.0.0", null)]
    [InlineData(1, "2.0.0", null)]
    [InlineData(2, "2.0.0", "cover")]
    public async Task Should_RefuseADeviceThatIsntARelayShelly(int? generation, string firmware, string? profile)
    {
        await using var simulator = Start(ShellyModels.TwoRelays, firmware: firmware);
        simulator.Generation = generation;
        simulator.Profile = profile;
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays);

        await Wait.UntilAsync(() => run.Client.State.Problem is not null);

        var kind = profile is null ? ConnectionProblemKind.WrongModel : ConnectionProblemKind.CoverProfile;
        Assert.Equal(new ConnectionProblem(kind, ShellyModels.TwoRelays), run.Client.State.Problem);
        Assert.False(run.Client.State.IsConnected);
        var reason = profile is null ? "isn't a Shelly of the second generation or later" : "set up to drive a cover";
        Assert.Contains(run.Traces, t => t.Text.Contains(reason, StringComparison.Ordinal) && t.IsError);
        Assert.DoesNotContain(ShellyApi.SwitchSet, simulator.Received);
    }

    [Theory]
    [InlineData("1.6.2")]
    [InlineData("0.6.7")]
    [InlineData("beta")]
    public async Task Should_RefuseFirmwareBefore170(string firmware)
    {
        await using var simulator = Start(ShellyModels.OneRelay, firmware: firmware);
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text.StartsWith("Connection lost: The Shelly's firmware is too old. Update it to 1.7.0", StringComparison.Ordinal)));

        Assert.False(run.Client.State.IsConnected);
        Assert.Equal(new ConnectionProblem(ConnectionProblemKind.FirmwareTooOld, ShellyModels.OneRelay, MinFirmware: "1.7.0"), run.Client.State.Problem);
        Assert.Equal([ShellyApi.GetDeviceInfo], simulator.Received);
    }

    [Fact]
    public async Task Should_TakeFirmware170()
    {
        await using var simulator = Start(ShellyModels.OneRelay, firmware: "1.7.0");
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay);

        await Wait.UntilAsync(() => run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_RefuseADeviceWithAnotherNumberOfRelays()
    {
        await using var simulator = Start(ShellyModels.FourRelays, Password);
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays, Password);

        await Wait.UntilAsync(() => run.Client.State.Problem is not null);

        Assert.Equal(new ConnectionProblem(ConnectionProblemKind.WrongModel, ShellyModels.TwoRelays, ShellyModels.FourRelays, FoundRelayCount: 4), run.Client.State.Problem);
        Assert.Contains(run.Traces, t => t.Text == "The Shelly at this address has 4 relays, not 2." && t.IsError);
        Assert.False(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_LoseADevice_WhoseSwitchesChangeWhileItRuns()
    {
        await using var simulator = Start(ShellyModels.TwoRelays);
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        // Set to the cover profile at its own web page: three polls in a row fail, and the connect after says why.
        simulator.Profile = "cover";
        for (var poll = 0; poll < BoardPoller<ShellyStatus>.DefaultFailuresBeforeLost; poll++)
        {
            await Wait.UntilAsync(() => run.Time.WaitsOnly(run.PollInterval));
            run.Time.Advance(run.PollInterval);
        }

        await Wait.UntilAsync(() => !run.Client.State.IsConnected);
        await Wait.UntilAsync(() => run.Time.WaitsOnly(TimeSpan.FromSeconds(1)));
        run.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => run.Client.State.Problem is not null);
        Assert.Contains(run.Traces, t => t.Text.Contains("drive a cover", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_ShowTheDevicesOwnMessage_WhenItRefusesASwitchSet()
    {
        await using var simulator = Start(ShellyModels.TwoRelays, Password);
        simulator.FailRelays = 0b0010;
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays, Password, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        var error = await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0010, Cancel));
        Assert.Equal("The Shelly didn't take the command: relay 2: Overtemp condition present (error -109)", error.Message);
        Assert.Contains(run.Traces, t => t.Text == "Refused: Overtemp condition present (error -109)" && t.IsError);

        // The other relay still goes, and only it counts as taken.
        Assert.Equal(0b0001, await run.Client.TurnOnAsync(0b0011, Cancel));
        Assert.Equal(0b0001, simulator.Relays);

        // A refusal is an answer: never a lost device, however many come.
        for (var i = 0; i < BoardPoller<ShellyStatus>.DefaultFailuresBeforeLost + 1; i++)
        {
            await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOffAsync(0b0010, Cancel));
        }

        Assert.True(run.Client.State.IsConnected);
    }

    // A 429 part-way through a mask stops the rest, and the relays the device took before it are returned (the
    // IRelayBoard contract). The device is read again at once.
    [Fact]
    public async Task Should_ReturnTheRelaysTaken_WhenA429StopsTheRestOfTheMask()
    {
        await using var simulator = Start(ShellyModels.FourRelays, Password);
        await using var run = ClientRun.Start(simulator, ShellyModels.FourRelays, Password, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        var sent = simulator.Received.Count;
        simulator.AfterSwitchSet = _ => simulator.Throttle = true;

        Assert.Equal(0b0001, await run.Client.TurnOnAsync(0b0111, Cancel));

        // Relay 1, then the 429 for relay 2, and nothing for relay 3; then the read at once, throttled too.
        await Wait.UntilAsync(() => simulator.ThrottledCount == 2);
        Assert.Equal(0b0001, simulator.Relays);
        Assert.Equal(
            [$"{ShellyApi.SwitchSet} (signed)", $"{ShellyApi.SwitchSet} (signed)", $"{ShellyApi.GetStatus} (signed)"],
            simulator.Received.Skip(sent));
        Assert.Contains(run.Traces, t => t.Text.StartsWith("Stopped at relay 2, nothing more sent: ", StringComparison.Ordinal) && t.IsError);

        // Before any relay was taken, the same 429 fails the command.
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0100, Cancel));
    }

    // No answer part-way through a mask stops the rest too, and the relays the device took before it are returned.
    [Fact]
    public async Task Should_ReturnTheRelaysTaken_WhenNoAnswerStopsTheRestOfTheMask()
    {
        await using var simulator = Start(ShellyModels.FourRelays);
        await using var run = ClientRun.Start(simulator, ShellyModels.FourRelays, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        var sent = simulator.Received.Count;
        simulator.AfterSwitchSet = relay => simulator.RejectConnections = relay == 2;

        Assert.Equal(0b0011, await run.Client.TurnOnAsync(0b1011, Cancel));

        Assert.Equal(0b0011, simulator.Relays);
        Assert.Equal([ShellyApi.SwitchSet, ShellyApi.SwitchSet], simulator.Received.Skip(sent));
        Assert.Contains(run.Traces, t => t.Text.StartsWith("Stopped at relay 4, nothing more sent: ", StringComparison.Ordinal) && t.IsError);
    }

    [Fact]
    public async Task Should_Reconnect_AfterTheDeviceWasUnreachable()
    {
        await using var simulator = Start(ShellyModels.OneRelay, Password);
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, Password);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        simulator.RejectConnections = true;
        simulator.DropConnections();
        for (var poll = 1; poll <= BoardPoller<ShellyStatus>.DefaultFailuresBeforeLost; poll++)
        {
            Assert.True(run.Client.State.IsConnected);
            var failures = poll;
            await Wait.UntilAsync(() => run.Time.WaitsOnly(run.PollInterval));
            run.Time.Advance(run.PollInterval);
            await Wait.UntilAsync(() => run.Traces.Count(t => t.Text.StartsWith("No answer: ", StringComparison.Ordinal)) == failures);
        }

        await Wait.UntilAsync(() => !run.Client.State.IsConnected);
        Assert.Contains(run.Traces, t => t.Text.StartsWith("Connection lost: ", StringComparison.Ordinal) && t.IsError);

        // The states after a power cut are kept while disconnected, as the relays are.
        Assert.Equal(AllOff(1), run.Client.State.PowerOn);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOnAsync(0b0001, Cancel));

        simulator.RejectConnections = false;
        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "Trying again in 1 s") && run.Time.WaitsOnly(TimeSpan.FromSeconds(1)));
        run.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        await run.Client.TurnOnAsync(0b0001, Cancel);
        Assert.Equal(0b0001, simulator.Relays);
    }

    [Fact]
    public async Task Should_NeverTraceThePasswordOrTheDigest()
    {
        await using var simulator = Start(ShellyModels.OneRelay, Password);
        List<BoardTraceEntry> deviceSide = [];
        simulator.Traced += (_, entry) =>
        {
            lock (deviceSide)
            {
                deviceSide.Add(entry);
            }
        };
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, Password, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        await run.Client.TurnOnAsync(0b0001, Cancel);

        List<BoardTraceEntry> all;
        lock (deviceSide)
        {
            all = [.. run.Traces, .. deviceSide];
        }

        Assert.Contains(all, t => t.Bytes?.EndsWith(" · with the password's digest", StringComparison.Ordinal) == true);
        Assert.All(all, t => Assert.DoesNotContain(Password, $"{t.Text} {t.Bytes}", StringComparison.Ordinal));
        Assert.All(all, t => Assert.DoesNotContain("response=", $"{t.Text} {t.Bytes}", StringComparison.Ordinal));
        Assert.DoesNotContain(Password, run.Client.State.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_MoveToAnotherDevice_WhenTheAddressChanges()
    {
        await using var first = Start(ShellyModels.OneRelay);
        await using var second = Start(ShellyModels.OneRelay);
        await second.SwitchRelayAsync(1, true);
        await using var run = ClientRun.Start(first, ShellyModels.OneRelay);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        run.Client.SetEndpoint(new BoardEndpoint("127.0.0.1", second.Port));

        await Wait.UntilAsync(() => run.Client.State.IsOn(1));
        Assert.Equal(second.Port, run.Client.Endpoint.Port);
    }

    [Fact]
    public async Task Should_RefuseCommands_BeforeItConnected_AndRelaysTheEntryHasNot()
    {
        using var client = CreateClient(1, ShellyModels.TwoRelays);

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => client.TurnOnAsync(0b0001, Cancel));
        Assert.Throws<BoardNotConnectedException>(client.Refresh);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.TurnOnAsync(0b0100, Cancel));
    }

    [Fact]
    public void Should_RefuseSettingsItCantUse()
    {
        Assert.Throws<ArgumentException>(() => CreateClient(80, ShellyModels.OneRelay, password: new string('p', ShellyApi.PasswordMaxLength + 1)));
        Assert.Throws<ArgumentException>(() => CreateClient(80, ShellyModels.OneRelay, pollSeconds: 0));
        Assert.Throws<ArgumentException>(() => CreateClient(80, Contactor.Boards.Devantech.DevantechModels.Eth002));
        using var client = CreateClient(80, ShellyModels.OneRelay, host: "");
        Assert.False(client.Endpoint.HasHost);
        Assert.Throws<ArgumentException>(() => client.SetPassword(new string('p', ShellyApi.PasswordMaxLength + 1)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_StopReading_AHugeAnswer(bool contentLength)
    {
        // A megabyte, with or without saying so up front: the client reads at most 64 kB of it.
        await using var server = CannedDevice.Start(new byte[1024 * 1024], contentLength);
        await using var run = ClientRun.Start(server.Port, ShellyModels.OneRelay);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text.Contains("longer than 65536 bytes", StringComparison.Ordinal)));

        Assert.False(run.Client.State.IsConnected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"id\":999,\"result\":{\"id\":\"shelly-x\",\"gen\":2,\"ver\":\"2.0.0\"}}")]
    public async Task Should_NeverConnect_WhenTheAnswerIsBroken(string body)
    {
        await using var server = CannedDevice.Start(Encoding.UTF8.GetBytes(body), contentLength: true);
        await using var run = ClientRun.Start(server.Port, ShellyModels.OneRelay);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text.StartsWith("Connection lost: The Shelly's answer", StringComparison.Ordinal)));

        Assert.False(run.Client.State.IsConnected);
        Assert.Null(run.Client.State.Status);
    }

    [Fact]
    public async Task Should_GiveUpOnARequest_TheDeviceNeverAnswers()
    {
        await using var simulator = Start(ShellyModels.OneRelay);
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay);
        var time = run.Time;
        await Wait.UntilAsync(() => run.Client.State.IsConnected && time.WaitsOnly(run.PollInterval));

        // A device that takes the connection and says nothing: its own wait for the request is longer than ours.
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        run.Client.SetEndpoint(new BoardEndpoint("127.0.0.1", ((IPEndPoint)silent.LocalEndpoint).Port));
        await Wait.UntilAsync(() => time.WaitsOnly(ShellyClient.RequestTimeout));
        time.Advance(ShellyClient.RequestTimeout);

        await Wait.UntilAsync(() => run.Traces.Any(t => t.Text == "No answer: The Shelly did not answer within 5 s." && t.IsError));
    }

    [Fact]
    public async Task Should_ReadEachSwitchsPowerOnState_AtConnect()
    {
        await using var simulator = Start(ShellyModels.FourRelays);
        simulator.SetInitialState(1, "on");
        simulator.SetInitialState(2, "restore_last");
        simulator.SetInitialState(3, "match_input");
        simulator.SetInitialState(4, "something_new");
        await using var run = ClientRun.Start(simulator, ShellyModels.FourRelays);

        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        // A value the docs don't list is unknown for that relay only.
        Assert.Equal(new PowerOnStates([PowerOnState.On, PowerOnState.AsBefore, PowerOnState.FollowsInput, null]), run.Client.State.PowerOn);
        Assert.Contains(run.Traces, t => t.Text == "Ask for relay 2's settings" && t.Direction == BoardTraceDirection.Sent);
        Assert.Contains(run.Traces, t => t.Text == "Relay 2 after a power cut: as before" && t.Direction == BoardTraceDirection.Received);
        Assert.Contains(run.Traces, t => t.Text == "Relay 4 after a power cut: unknown");
        Assert.Empty(run.PowerOnChanges);
    }

    [Fact]
    public async Task Should_ReadTheSettingsAgain_OnlyWhenCfgRevChanges()
    {
        await using var simulator = Start(ShellyModels.TwoRelays, Password);
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays, Password);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        var reads = GetConfigs(simulator);
        Assert.Equal(2, reads);

        await run.PollAsync();
        await run.PollAsync();
        Assert.Equal(reads, GetConfigs(simulator));

        // Changed at the device: the next poll reads every switch again, and tells only the power-on states changed.
        var changes = run.Changes.Count;
        simulator.SetInitialState(2, "on");
        await run.PollAsync();
        Assert.Equal(reads + 2, GetConfigs(simulator));
        Assert.Equal(new PowerOnStates([PowerOnState.Off, PowerOnState.On]), run.Client.State.PowerOn);
        Assert.Equal(new PowerOnStates([PowerOnState.Off, PowerOnState.On]), Assert.Single(run.PowerOnChanges).PowerOn);
        Assert.Equal(changes, run.Changes.Count);

        // Another setting: read again, but nothing to tell.
        simulator.ChangeAnotherSetting();
        await run.PollAsync();
        await run.PollAsync();
        Assert.Equal(reads + 4, GetConfigs(simulator));
        Assert.Single(run.PowerOnChanges);
        Assert.Equal(0, simulator.DeniedCount);
    }

    [Fact]
    public async Task Should_SetOneRelaysPowerOnState_AndReadItBack()
    {
        await using var simulator = Start(ShellyModels.FourRelays);
        await using var run = ClientRun.Start(simulator, ShellyModels.FourRelays, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        var before = simulator.Received.Count;
        var revision = simulator.ConfigRevision;

        await run.Client.SetPowerOnAsync(3, PowerOnState.On, Cancel);

        Assert.Equal(["off", "off", "on", "off"], Enumerable.Range(1, 4).Select(simulator.InitialState));
        Assert.Equal(revision + 1, simulator.ConfigRevision);

        // One Switch.SetConfig for that switch, then the status and every switch's settings, in the same command.
        Assert.Equal(
            [ShellyApi.SwitchSetConfig, ShellyApi.GetStatus, .. Enumerable.Repeat(ShellyApi.SwitchGetConfig, 4)],
            simulator.Received.Skip(before));
        Assert.Equal(new PowerOnStates([PowerOnState.Off, PowerOnState.Off, PowerOnState.On, PowerOnState.Off]), run.Client.State.PowerOn);
        Assert.Equal(PowerOnState.On, Assert.Single(run.PowerOnChanges).PowerOn!.For(3));
        Assert.Contains(run.Traces, t => t.Text == "Set relay 3 after a power cut: on" && t.Direction == BoardTraceDirection.Sent);
        Assert.Contains(run.Traces, t => t.Text == "Relay 3's setting saved");

        // The same again: the device takes it and cfg_rev stays, but the client still reads it back.
        await run.Client.SetPowerOnAsync(3, PowerOnState.On, Cancel);
        Assert.Equal(revision + 1, simulator.ConfigRevision);
        Assert.Equal(8, simulator.Received.Skip(before).Count(r => r == ShellyApi.SwitchGetConfig));
        Assert.Single(run.PowerOnChanges);
    }

    [Theory]
    [InlineData(PowerOnState.Off, "off")]
    [InlineData(PowerOnState.AsBefore, "restore_last")]
    [InlineData(PowerOnState.On, "on")]
    [InlineData(PowerOnState.FollowsInput, "match_input")]
    public async Task Should_SetEachState_AsTheDocsNameIt(PowerOnState state, string initialState)
    {
        await using var simulator = Start(ShellyModels.OneRelay);
        simulator.SetInitialState(1, state == PowerOnState.On ? "off" : "on");
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        await run.Client.SetPowerOnAsync(1, state, Cancel);

        Assert.Equal(initialState, simulator.InitialState(1));
        Assert.Equal(state, run.Client.State.PowerOn!.For(1));
    }

    [Fact]
    public async Task Should_RefuseAPowerOnState_BeforeSendingAnything()
    {
        using var unconnected = CreateClient(1, ShellyModels.TwoRelays);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => unconnected.SetPowerOnAsync(1, PowerOnState.Off, Cancel));

        await using var simulator = Start(ShellyModels.TwoRelays);
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        var before = simulator.Received.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.SetPowerOnAsync(0, PowerOnState.Off, Cancel));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.SetPowerOnAsync(3, PowerOnState.Off, Cancel));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.SetPowerOnAsync(1, (PowerOnState)4, Cancel));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.SetPowerOnAsync(1, (PowerOnState)(-1), Cancel));

        Assert.Equal(before, simulator.Received.Count);
    }

    [Fact]
    public async Task Should_SayTheDeviceRefusedTheSetting_AndStayConnected()
    {
        await using var simulator = Start(ShellyModels.TwoRelays);
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        simulator.FailConfigRelays = 0b10;

        var refused = await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.SetPowerOnAsync(2, PowerOnState.On, Cancel));

        Assert.Equal("The Shelly didn't take the setting: relay 2: Overtemp condition present (error -109)", refused.Message);
        Assert.Equal("off", simulator.InitialState(2));

        // The poller reads the device at once after a refusal, settings included: relay 2's is refused now, so unknown.
        await Wait.UntilAsync(() => run.Client.State.PowerOn == new PowerOnStates([PowerOnState.Off, null]));
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_LeaveARelayUnknown_WhenTheDeviceRefusesItsSettings()
    {
        await using var simulator = Start(ShellyModels.TwoRelays);
        simulator.FailConfigRelays = 0b01;
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays);

        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        Assert.Equal(new PowerOnStates([null, PowerOnState.Off]), run.Client.State.PowerOn);
    }

    // The read that confirms a Switch.Set is the one a group's next step waits for: it never reads the settings, also
    // when cfg_rev moved. The next poll does.
    [Fact]
    public async Task Should_NeverReadTheSettings_InTheReadThatConfirmsASwitch()
    {
        await using var simulator = Start(ShellyModels.FourRelays);
        await using var run = ClientRun.Start(simulator, ShellyModels.FourRelays, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        simulator.ChangeAnotherSetting();
        var before = simulator.Received.Count;

        Assert.Equal(0b0001, await run.Client.TurnOnAsync(0b0001, Cancel));

        Assert.Equal([ShellyApi.SwitchSet, ShellyApi.GetStatus], simulator.Received.Skip(before));
        Assert.Equal(AllOff(4), run.Client.State.PowerOn);

        await run.PollAsync();
        Assert.Equal(4, simulator.Received.Skip(before).Count(r => r == ShellyApi.SwitchGetConfig));
    }

    [Fact]
    public async Task Should_Connect_WhenASwitchsSettingsCantBeRead()
    {
        await using var simulator = Start(ShellyModels.TwoRelays);
        simulator.UnreadableConfigRelays = 0b01;
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays);

        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        // The device answered: the next switch is still asked.
        Assert.Equal(new PowerOnStates([null, PowerOnState.Off]), run.Client.State.PowerOn);
        Assert.DoesNotContain(run.Traces, t => t.Text.StartsWith("Connection lost", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_Connect_WhenASettingsRequestGetsNoAnswer_AndReadAgainAMinuteLater()
    {
        await using var simulator = Start(ShellyModels.TwoRelays);
        simulator.SilentConfigRelays = 0b01;
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays, pollSeconds: 30);

        // Every request has the same timeout: wait until it is the settings request that waits.
        await Wait.UntilAsync(() => simulator.Received.Contains(ShellyApi.SwitchGetConfig) && run.Time.WaitsOnly(ShellyClient.RequestTimeout));
        run.Time.Advance(ShellyClient.RequestTimeout);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        // No answer stops the read: relay 2 isn't asked, and both are unknown.
        Assert.Equal(new PowerOnStates([null, null]), run.Client.State.PowerOn);
        Assert.Equal(1, simulator.Received.Count(r => r == ShellyApi.SwitchGetConfig));
        Assert.Contains(run.Traces, t => t.Text == "Settings not read from relay 1 on: unknown after a power cut" && t.IsError);

        // Not at the next poll, 30 s on; at the one a minute after the read.
        simulator.SilentConfigRelays = 0;
        await run.PollAsync();
        Assert.Equal(1, simulator.Received.Count(r => r == ShellyApi.SwitchGetConfig));
        await run.PollAsync();
        Assert.Equal(AllOff(2), run.Client.State.PowerOn);
        Assert.Equal(3, simulator.Received.Count(r => r == ShellyApi.SwitchGetConfig));
    }

    [Fact]
    public async Task Should_StayConnected_WhenAPollsSettingsReadFails()
    {
        await using var simulator = Start(ShellyModels.TwoRelays);
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        simulator.UnreadableConfigRelays = 0b11;
        simulator.ChangeAnotherSetting();
        var reads = simulator.Received.Count(r => r == ShellyApi.SwitchGetConfig);

        // Each poll is answered: a broken settings read never loses the device.
        for (var poll = 0; poll < BoardPoller<ShellyStatus>.DefaultFailuresBeforeLost + 1; poll++)
        {
            await run.PollAsync();
        }

        Assert.True(run.Client.State.IsConnected);
        Assert.Equal(new PowerOnStates([null, null]), run.Client.State.PowerOn);

        // Read once after the change, and not again within the minute.
        Assert.Equal(reads + 2, simulator.Received.Count(r => r == ShellyApi.SwitchGetConfig));
    }

    [Fact]
    public async Task Should_ReadTheSettingsEveryMinute_OnADeviceWithoutCfgRev()
    {
        await using var simulator = Start(ShellyModels.OneRelay);
        simulator.ReportsConfigRevision = false;
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, pollSeconds: 30);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        Assert.Equal(1, simulator.Received.Count(r => r == ShellyApi.SwitchGetConfig));

        simulator.SetInitialState(1, "on");
        await run.PollAsync();
        Assert.Equal(1, simulator.Received.Count(r => r == ShellyApi.SwitchGetConfig));

        await run.PollAsync();
        Assert.Equal(2, simulator.Received.Count(r => r == ShellyApi.SwitchGetConfig));
        Assert.Equal(PowerOnState.On, run.Client.State.PowerOn!.For(1));
    }

    [Fact]
    public async Task Should_TraceSettingsReads_AsRoutine_UnlessAStateChanged()
    {
        await using var simulator = Start(ShellyModels.TwoRelays);
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        var before = run.Traces.Count;
        simulator.ChangeAnotherSetting();
        await run.PollAsync();
        var same = run.Traces.Skip(before).ToList();
        Assert.Equal(2, same.Count(t => t.Text.StartsWith("Ask for relay", StringComparison.Ordinal) && t.IsRoutine));
        Assert.Equal(2, same.Count(t => t.Text.EndsWith("after a power cut: off", StringComparison.Ordinal) && t.IsRoutine));
        Assert.DoesNotContain(same, t => !t.IsRoutine);

        before = run.Traces.Count;
        simulator.SetInitialState(2, "on");
        await run.PollAsync();
        var changed = run.Traces.Skip(before).ToList();
        Assert.Contains(changed, t => t.Text == "Relay 1 after a power cut: off" && t.IsRoutine);
        Assert.Contains(changed, t => t.Text == "Relay 2 after a power cut: on" && !t.IsRoutine);
    }

    [Fact]
    public async Task Should_ReadSchedulesAndAutoOn_AtConnect_AsRulesWithAUnitPerSchedule()
    {
        await using var simulator = Start(ShellyModels.FourRelays);
        simulator.AddSchedule("""[{"method":"Switch.Set","params":{"id":0,"on":true}},{"method":"Switch.SetMany","params":{"ids":[1],"on":true}}]""");
        simulator.AddSchedule("""[{"method":"Switch.Toggle","params":{"id":1}}]""");
        simulator.AddSchedule("""[{"method":"Switch.SetAll","params":{"on":true}}]""", enable: false);
        simulator.AddSchedule("""[{"method":"Shelly.Reboot"}]""");
        simulator.SetAutoOn(3, true);
        await using var run = ClientRun.Start(simulator, ShellyModels.FourRelays);

        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        OwnRule timer1 = new(1, OwnRuleKind.Timer), timer2 = new(2, OwnRuleKind.Timer), autoOn3 = new(3, OwnRuleKind.AutoOn);
        var rules = run.Client.State.OwnRules!;
        Assert.Equal(new OwnRules([timer1, timer2, autoOn3], [[timer1, timer2], [timer2], [autoOn3]]), rules);

        // Relay 2's timer is also behind the second schedule: turning off relay 1's leaves it. Relay 2's takes both
        // schedules, so relay 1's goes with it.
        Assert.Empty(rules.GoesWith(timer1));
        Assert.Equal([timer1], rules.GoesWith(timer2));
        Assert.Empty(rules.GoesWith(autoOn3));
        Assert.Equal(1, simulator.Received.Count(r => r == ShellyApi.ScheduleList));
        Assert.Contains(run.Traces, t => t.Text == "Ask for the schedules" && t.Direction == BoardTraceDirection.Sent);
        Assert.Contains(run.Traces, t => t.Text == "Schedules: 2 switch relay 1 and 2" && t.Direction == BoardTraceDirection.Received);
        Assert.Contains(run.Traces, t => t.Text == "Relay 3 after a power cut: off · turns on again by itself");
        Assert.Empty(run.OwnRulesChanges);
    }

    [Fact]
    public async Task Should_ReadTheSchedulesAgain_OnlyWhenScheduleRevChanges()
    {
        await using var simulator = Start(ShellyModels.TwoRelays, Password);
        var job = simulator.AddSchedule("""[{"method":"Switch.Set","params":{"id":1,"on":true}}]""");
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays, Password);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        Assert.Equal(new OwnRules([new OwnRule(2, OwnRuleKind.Timer)], [[new OwnRule(2, OwnRuleKind.Timer)]]), run.Client.State.OwnRules);

        await run.PollAsync();
        await run.PollAsync();
        Assert.Equal(1, simulator.Received.Count(r => r.StartsWith(ShellyApi.ScheduleList, StringComparison.Ordinal)));

        // Turned off at the device: the next poll reads the schedules again, and tells only the rules changed.
        var changes = run.Changes.Count;
        simulator.SetScheduleEnabled(job, false);
        await run.PollAsync();
        Assert.Equal(2, simulator.Received.Count(r => r.StartsWith(ShellyApi.ScheduleList, StringComparison.Ordinal)));
        Assert.Equal(OwnRules.Empty, run.Client.State.OwnRules);
        Assert.Equal(OwnRules.Empty, Assert.Single(run.OwnRulesChanges).OwnRules);
        Assert.Equal(changes, run.Changes.Count);
        Assert.Empty(run.PowerOnChanges);

        // Auto-on comes with the settings, at the next cfg_rev; not the schedules.
        simulator.SetAutoOn(1, true);
        await run.PollAsync();
        Assert.Equal(new OwnRules([new OwnRule(1, OwnRuleKind.AutoOn)], [[new OwnRule(1, OwnRuleKind.AutoOn)]]), run.Client.State.OwnRules);
        Assert.Equal(2, run.OwnRulesChanges.Count);
        Assert.Empty(run.PowerOnChanges);
        Assert.Equal(2, simulator.Received.Count(r => r.StartsWith(ShellyApi.ScheduleList, StringComparison.Ordinal)));
        Assert.Equal(changes, run.Changes.Count);
    }

    [Fact]
    public async Task Should_ReadTheSchedulesEveryMinute_OnADeviceWithoutScheduleRev()
    {
        await using var simulator = Start(ShellyModels.OneRelay);
        simulator.ReportsScheduleRevision = false;
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, pollSeconds: 30);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        Assert.Equal(1, simulator.Received.Count(r => r == ShellyApi.ScheduleList));

        simulator.AddSchedule("""[{"method":"Switch.Set","params":{"id":0,"on":true}}]""");
        await run.PollAsync();
        Assert.Equal(1, simulator.Received.Count(r => r == ShellyApi.ScheduleList));

        await run.PollAsync();
        Assert.Equal(2, simulator.Received.Count(r => r == ShellyApi.ScheduleList));
        Assert.Equal([new OwnRule(1, OwnRuleKind.Timer)], run.Client.State.OwnRules!);
    }

    // The rules are read before the device's first connected state, so unknown rules while it answers mean they couldn't
    // be read, which the screens say (issue #551); a refused read is in the test below.
    [Fact]
    public async Task Should_ReadTheRules_BeforeTheFirstConnectedState()
    {
        await using var simulator = Start(ShellyModels.TwoRelays);
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays);

        await Wait.UntilAsync(() => run.Changes.Any(s => s.IsConnected));

        Assert.Equal(OwnRules.Empty, run.Changes.First(s => s.IsConnected).OwnRules);
    }

    [Fact]
    public async Task Should_LeaveTheRulesUnknown_WhenTheSchedulesCantBeRead_AndStayConnected()
    {
        await using var simulator = Start(ShellyModels.TwoRelays);
        simulator.FailSchedules = true;
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays, pollSeconds: 30);

        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        Assert.Null(run.Client.State.OwnRules);
        Assert.Equal(AllOff(2), run.Client.State.PowerOn);
        Assert.Contains(run.Traces, t => t.Text.StartsWith("Schedules not read", StringComparison.Ordinal) && t.IsError && !t.IsRoutine);

        // Each poll is answered; the read again a minute after, not at every poll, and quietly while it still fails.
        for (var poll = 0; poll < BoardPoller<ShellyStatus>.DefaultFailuresBeforeLost + 1; poll++)
        {
            await run.PollAsync();
        }

        Assert.True(run.Client.State.IsConnected);
        Assert.Equal(3, simulator.Received.Count(r => r == ShellyApi.ScheduleList));
        Assert.Single(run.Traces, t => t.Text.StartsWith("Schedules not read", StringComparison.Ordinal) && !t.IsRoutine);

        simulator.FailSchedules = false;
        await run.PollAsync();
        await run.PollAsync();
        Assert.Equal(OwnRules.Empty, run.Client.State.OwnRules);
        Assert.Equal(OwnRules.Empty, Assert.Single(run.OwnRulesChanges).OwnRules);
    }

    [Fact]
    public async Task Should_LeaveTheRulesUnknown_WhenASwitchsSettingsCantBeRead()
    {
        await using var simulator = Start(ShellyModels.TwoRelays);
        simulator.FailConfigRelays = 0b10;
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays);

        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        // Relay 2's auto-on isn't known, so neither are the rules; its state after a power cut is unknown alone.
        Assert.Null(run.Client.State.OwnRules);
        Assert.Equal(new PowerOnStates([PowerOnState.Off, null]), run.Client.State.PowerOn);
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_SkipTheSchedules_WhenTheSettingsGetNoAnswer()
    {
        await using var simulator = Start(ShellyModels.OneRelay);
        simulator.SilentConfigRelays = 0b1;
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, pollSeconds: 30);

        await Wait.UntilAsync(() => simulator.Received.Contains(ShellyApi.SwitchGetConfig) && run.Time.WaitsOnly(ShellyClient.RequestTimeout));
        run.Time.Advance(ShellyClient.RequestTimeout);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        // The device didn't answer: the schedules aren't asked for at once, and the rules are unknown.
        Assert.DoesNotContain(ShellyApi.ScheduleList, simulator.Received);
        Assert.Null(run.Client.State.OwnRules);

        simulator.SilentConfigRelays = 0;
        await run.PollAsync();
        await run.PollAsync();
        Assert.Equal(1, simulator.Received.Count(r => r == ShellyApi.ScheduleList));
        Assert.Equal(OwnRules.Empty, run.Client.State.OwnRules);
    }

    [Fact]
    public async Task Should_NeverReadTheSchedules_InTheReadThatConfirmsASwitch()
    {
        await using var simulator = Start(ShellyModels.TwoRelays);
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        simulator.AddSchedule("""[{"method":"Switch.Set","params":{"id":0,"on":true}}]""");
        var before = simulator.Received.Count;

        Assert.Equal(0b01, await run.Client.TurnOnAsync(0b01, Cancel));

        Assert.Equal([ShellyApi.SwitchSet, ShellyApi.GetStatus], simulator.Received.Skip(before));
        Assert.Equal(OwnRules.Empty, run.Client.State.OwnRules);

        await run.PollAsync();
        Assert.Equal([new OwnRule(1, OwnRuleKind.Timer)], run.Client.State.OwnRules!);
    }

    [Fact]
    public async Task Should_TurnOffEveryScheduleBehindATimer_AndReadThemBack()
    {
        await using var simulator = Start(ShellyModels.ThreeRelays);
        var both = simulator.AddSchedule("""[{"method":"Switch.Set","params":{"id":0,"on":true}},{"method":"Switch.Set","params":{"id":1,"on":true}}]""");
        var two = simulator.AddSchedule("""[{"method":"Switch.Set","params":{"id":1,"on":false}}]""");
        var three = simulator.AddSchedule("""[{"method":"Switch.Set","params":{"id":2,"on":true}}]""");
        await using var run = ClientRun.Start(simulator, ShellyModels.ThreeRelays, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        var before = simulator.Received.Count;
        var changes = run.Changes.Count;

        var taken = await run.Client.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.Timer)], Cancel);

        // Only the schedule behind relay 1's timer, never deleted or rewritten; then the schedules read back at once.
        Assert.Equal([new OwnRule(1, OwnRuleKind.Timer)], taken);
        Assert.Equal([ShellyApi.ScheduleUpdate, ShellyApi.GetStatus, ShellyApi.ScheduleList], simulator.Received.Skip(before));
        Assert.Equal((false, true, true), (simulator.ScheduleEnabled(both), simulator.ScheduleEnabled(two), simulator.ScheduleEnabled(three)));
        Assert.Equal([new OwnRule(2, OwnRuleKind.Timer), new OwnRule(3, OwnRuleKind.Timer)], run.Client.State.OwnRules!);
        Assert.Single(run.OwnRulesChanges);
        Assert.Equal(changes, run.Changes.Count);
        Assert.Contains(run.Traces, t => t.Text == "Turn off schedule 1" && t.Direction == BoardTraceDirection.Sent);
        Assert.Contains(run.Traces, t => t.Text == "Schedule 1 is off");

        // Relay 2's timer and relay 3's, given twice: the one schedule still behind each, once each.
        before = simulator.Received.Count;
        taken = await run.Client.TurnOffOwnRulesAsync([new OwnRule(3, OwnRuleKind.Timer), new OwnRule(2, OwnRuleKind.Timer), new OwnRule(3, OwnRuleKind.Timer)], Cancel);
        Assert.Equal([new OwnRule(2, OwnRuleKind.Timer), new OwnRule(3, OwnRuleKind.Timer)], taken);
        Assert.Equal([ShellyApi.ScheduleUpdate, ShellyApi.ScheduleUpdate, ShellyApi.GetStatus, ShellyApi.ScheduleList], simulator.Received.Skip(before));
        Assert.Equal(OwnRules.Empty, run.Client.State.OwnRules);
    }

    [Fact]
    public async Task Should_TurnOffASharedSchedule_WithTheRulesThatGoWithIt()
    {
        await using var simulator = Start(ShellyModels.TwoRelays);
        var shared = simulator.AddSchedule("""[{"method":"Switch.SetAll","params":{"on":true}}]""");
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        OwnRule timer1 = new(1, OwnRuleKind.Timer), timer2 = new(2, OwnRuleKind.Timer);
        Assert.Equal([timer2], run.Client.State.OwnRules!.GoesWith(timer1));

        Assert.Equal([timer1], await run.Client.TurnOffOwnRulesAsync([timer1], Cancel));

        // The one schedule switched both: relay 2's timer went with it, as GoesWith said, but isn't returned.
        Assert.False(simulator.ScheduleEnabled(shared));
        Assert.Equal(OwnRules.Empty, run.Client.State.OwnRules);
    }

    [Fact]
    public async Task Should_TurnOffAutoOn_ForThatSwitchOnly_AndReadItBack()
    {
        await using var simulator = Start(ShellyModels.TwoRelays);
        simulator.SetAutoOn(1, true);
        simulator.SetAutoOn(2, true);
        simulator.SetInitialState(2, "restore_last");
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        var before = simulator.Received.Count;

        Assert.Equal([new OwnRule(2, OwnRuleKind.AutoOn)], await run.Client.TurnOffOwnRulesAsync([new OwnRule(2, OwnRuleKind.AutoOn)], Cancel));

        // One Switch.SetConfig with auto_on alone, then the status and every switch's settings, in the same command.
        Assert.Equal([ShellyApi.SwitchSetConfig, ShellyApi.GetStatus, ShellyApi.SwitchGetConfig, ShellyApi.SwitchGetConfig], simulator.Received.Skip(before));
        Assert.Equal((true, false), (simulator.AutoOn(1), simulator.AutoOn(2)));
        Assert.Equal("restore_last", simulator.InitialState(2));
        Assert.Equal([new OwnRule(1, OwnRuleKind.AutoOn)], run.Client.State.OwnRules!);
        Assert.Contains(run.Traces, t => t.Text == "Turn off relay 2's auto-on" && t.Direction == BoardTraceDirection.Sent);
        Assert.Contains(run.Traces, t => t.Text == "Relay 2's auto-on is off");
        Assert.Empty(run.PowerOnChanges);
    }

    [Fact]
    public async Task Should_TakeATimerNoScheduleSwitchesAnyMore_WithoutSendingIt()
    {
        await using var simulator = Start(ShellyModels.TwoRelays);
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        var before = simulator.Received.Count;

        Assert.Equal([new OwnRule(1, OwnRuleKind.Timer)], await run.Client.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.Timer)], Cancel));

        // Already gone as last read: nothing to turn off, and the schedules read back to confirm it.
        Assert.Equal([ShellyApi.GetStatus, ShellyApi.ScheduleList], simulator.Received.Skip(before));
    }

    [Fact]
    public async Task Should_RefuseRulesItCantTurnOff_BeforeSendingAnything()
    {
        using var unconnected = CreateClient(1, ShellyModels.TwoRelays);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => unconnected.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.Timer)], Cancel));
        Assert.Empty(await unconnected.TurnOffOwnRulesAsync([], Cancel));

        await using var simulator = Start(ShellyModels.TwoRelays);
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        var before = simulator.Received.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.Timer), new OwnRule(3, OwnRuleKind.Timer)], Cancel));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.AutoOn), new OwnRule(1, OwnRuleKind.InputLinkage)], Cancel));
        await Assert.ThrowsAsync<ArgumentException>(() => run.Client.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.Timer), null!], Cancel));
        await Assert.ThrowsAsync<ArgumentNullException>(() => run.Client.TurnOffOwnRulesAsync(null!, Cancel));
        Assert.Empty(await run.Client.TurnOffOwnRulesAsync([], Cancel));

        Assert.Equal(before, simulator.Received.Count);
    }

    [Fact]
    public async Task Should_SayTheDeviceDidntTakeIt_AndReturnOnlyWhatItTook()
    {
        await using var simulator = Start(ShellyModels.TwoRelays);
        var first = simulator.AddSchedule("""[{"method":"Switch.Set","params":{"id":0,"on":true}}]""");
        var second = simulator.AddSchedule("""[{"method":"Switch.Toggle","params":{"id":0}}]""");
        simulator.SetAutoOn(2, true);
        await using var run = ClientRun.Start(simulator, ShellyModels.TwoRelays, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        simulator.FailScheduleUpdates = new HashSet<long> { first };

        // One of the two schedules behind relay 1's timer refused: the timer isn't taken, the other schedule still went.
        var refused = await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.Timer)], Cancel));
        Assert.Equal("The Shelly didn't turn its rules off: schedule 1: Overtemp condition present (error -109)", refused.Message);
        Assert.Equal((true, false), (simulator.ScheduleEnabled(first), simulator.ScheduleEnabled(second)));

        // With an auto-on that goes: only that is returned.
        Assert.Equal(
            [new OwnRule(2, OwnRuleKind.AutoOn)],
            await run.Client.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.Timer), new OwnRule(2, OwnRuleKind.AutoOn)], Cancel));
        Assert.True(simulator.ScheduleEnabled(first));
        Assert.False(simulator.AutoOn(2));

        // A refusal is an answer: the device stays connected, and the rules show what is still there.
        await Wait.UntilAsync(() => run.Client.State.OwnRules == new OwnRules([new OwnRule(1, OwnRuleKind.Timer)], [[new OwnRule(1, OwnRuleKind.Timer)]]));
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_SendNothing_WhileTheSchedulesArentKnown()
    {
        await using var simulator = Start(ShellyModels.OneRelay);
        simulator.FailSchedules = true;
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        var before = simulator.Received.Count;

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.Timer)], Cancel));

        Assert.DoesNotContain(ShellyApi.ScheduleUpdate, simulator.Received.Skip(before));
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_StopAndCountAFailure_WhenAScheduleUpdateGetsNoAnswer()
    {
        await using var simulator = Start(ShellyModels.OneRelay);
        var first = simulator.AddSchedule("""[{"method":"Switch.Set","params":{"id":0,"on":true}}]""");
        var silent = simulator.AddSchedule("""[{"method":"Switch.Toggle","params":{"id":0}}]""");
        var last = simulator.AddSchedule("""[{"method":"Switch.Set","params":{"id":0,"on":false}}]""");
        simulator.SilentScheduleUpdates = new HashSet<long> { silent };
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        var turnOff = run.Client.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.Timer)], Cancel);
        await Wait.UntilAsync(() => simulator.Received.Count(r => r == ShellyApi.ScheduleUpdate) == 2 && run.Time.Waits(ShellyClient.RequestTimeout));

        // From here the device answers nothing: the read the poller makes at once after the command fails too.
        simulator.RejectConnections = true;
        run.Time.Advance(ShellyClient.RequestTimeout);
        var failed = await Assert.ThrowsAsync<BoardNotConnectedException>(() => turnOff);

        // No answer stops the rest: the last schedule is never asked.
        Assert.Equal("The Shelly didn't answer; its rules may not have been turned off.", failed.Message);
        Assert.IsType<TimeoutException>(failed.InnerException);
        Assert.Equal((false, true, true), (simulator.ScheduleEnabled(first), simulator.ScheduleEnabled(silent), simulator.ScheduleEnabled(last)));
        Assert.Equal(2, simulator.Received.Count(r => r == ShellyApi.ScheduleUpdate));

        // The command counted as one failed poll and the read after it as a second: the next poll is the third, and
        // loses the device.
        await Wait.UntilAsync(() => run.Time.WaitsOnly(run.PollInterval));
        Assert.True(run.Client.State.IsConnected);
        run.Time.Advance(run.PollInterval);
        await Wait.UntilAsync(() => !run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_SendNoMoreScheduleUpdates_WhenTheAddressMovesPartway()
    {
        await using var first = Start(ShellyModels.OneRelay);
        await using var second = Start(ShellyModels.OneRelay);
        var one = first.AddSchedule("""[{"method":"Switch.Set","params":{"id":0,"on":true}}]""");
        var two = first.AddSchedule("""[{"method":"Switch.Set","params":{"id":0,"on":false}}]""");
        second.AddSchedule("""[{"method":"Switch.Set","params":{"id":0,"on":true}}]""");
        second.AddSchedule("""[{"method":"Switch.Set","params":{"id":0,"on":false}}]""");
        await using var run = ClientRun.Start(first, ShellyModels.OneRelay, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        // While the first schedule's update reaches the first device, the address moves to the second.
        first.Traced += (_, entry) =>
        {
            if (entry.Text == $"Turn off schedule {one}")
            {
                run.Client.SetEndpoint(new BoardEndpoint("127.0.0.1", second.Port));
            }
        };

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.Timer)], Cancel));

        Assert.Equal((false, true), (first.ScheduleEnabled(one), first.ScheduleEnabled(two)));
        Assert.Equal(1, first.Received.Count(r => r == ShellyApi.ScheduleUpdate));
        Assert.DoesNotContain(ShellyApi.ScheduleUpdate, second.Received);
    }

    [Fact]
    public async Task Should_TurnOffOnlyTheAutoOn_WhileTheSchedulesArentKnown()
    {
        await using var simulator = Start(ShellyModels.OneRelay);
        simulator.FailSchedules = true;
        simulator.SetAutoOn(1, true);
        await using var run = ClientRun.Start(simulator, ShellyModels.OneRelay, pollSeconds: 60);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        var before = simulator.Received.Count;

        var taken = await run.Client.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.Timer), new OwnRule(1, OwnRuleKind.AutoOn)], Cancel);

        // The timer can't be taken without the schedules: nothing goes for it, and the auto-on goes alone.
        Assert.Equal([new OwnRule(1, OwnRuleKind.AutoOn)], taken);
        Assert.False(simulator.AutoOn(1));
        Assert.DoesNotContain(ShellyApi.ScheduleUpdate, simulator.Received.Skip(before));
        Assert.Equal(1, simulator.Received.Skip(before).Count(r => r == ShellyApi.SwitchSetConfig));
    }

    private static int GetConfigs(ShellySimulator simulator)
    {
        return simulator.Received.Count(r => r.StartsWith(ShellyApi.SwitchGetConfig, StringComparison.Ordinal) && r.EndsWith("(signed)", StringComparison.Ordinal));
    }

    private static PowerOnStates AllOff(int relays)
    {
        return new PowerOnStates(Enumerable.Repeat<PowerOnState?>(PowerOnState.Off, relays));
    }

    private static ShellySimulator Start(BoardModel model, string? password = null, string firmware = "2.0.0")
    {
        var simulator = new ShellySimulator(model, password, NullLogger.Instance) { Firmware = firmware };
        simulator.Start();
        return simulator;
    }

    private static ShellyClient CreateClient(int port, BoardModel model, string password = "", int pollSeconds = 2, string host = "127.0.0.1", TimeProvider? time = null)
    {
        var options = new ShellyOptions { Host = host, Port = port, Password = password, PollSeconds = pollSeconds };
        return new ShellyClient(Options.Create(options), model, time ?? new ManualTimeProvider(), NullLogger<ShellyClient>.Instance);
    }

    private sealed class ClientRun : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _task;
        private readonly List<BoardTraceEntry> _traces = [];
        private readonly List<ShellyState> _changes = [];
        private readonly List<ShellyState> _powerOnChanges = [];
        private readonly List<ShellyState> _ownRulesChanges = [];

        private ClientRun(ShellyClient client, ManualTimeProvider time, TimeSpan pollInterval, CancellationTokenSource cts)
        {
            Client = client;
            Time = time;
            PollInterval = pollInterval;
            _cts = cts;
            client.Traced += (_, entry) =>
            {
                lock (_traces)
                {
                    _traces.Add(entry);
                }
            };
            client.StateChanged += (_, state) =>
            {
                lock (_changes)
                {
                    _changes.Add(state);
                }
            };
            client.PowerOnChanged += (_, state) =>
            {
                lock (_powerOnChanges)
                {
                    _powerOnChanges.Add(state);
                }
            };
            client.OwnRulesChanged += (_, state) =>
            {
                lock (_ownRulesChanges)
                {
                    _ownRulesChanges.Add(state);
                }
            };
            _task = client.RunAsync(cts.Token);
        }

        public ShellyClient Client { get; }

        public ManualTimeProvider Time { get; }

        public TimeSpan PollInterval { get; }

        public List<BoardTraceEntry> Traces
        {
            get
            {
                lock (_traces)
                {
                    return [.. _traces];
                }
            }
        }

        public List<ShellyState> Changes
        {
            get
            {
                lock (_changes)
                {
                    return [.. _changes];
                }
            }
        }

        public List<ShellyState> PowerOnChanges
        {
            get
            {
                lock (_powerOnChanges)
                {
                    return [.. _powerOnChanges];
                }
            }
        }

        public List<ShellyState> OwnRulesChanges
        {
            get
            {
                lock (_ownRulesChanges)
                {
                    return [.. _ownRulesChanges];
                }
            }
        }

        public static ClientRun Start(ShellySimulator simulator, BoardModel model, string password = "", int pollSeconds = 2)
        {
            return Start(simulator.Port, model, password, pollSeconds);
        }

        public static ClientRun Start(int port, BoardModel model, string password = "", int pollSeconds = 2)
        {
            var time = new ManualTimeProvider();
            return new ClientRun(CreateClient(port, model, password, pollSeconds, time: time), time, TimeSpan.FromSeconds(pollSeconds), new CancellationTokenSource());
        }

        /// <summary>
        /// Moves the clock to the next poll, once the client waits for it, and waits until the device answered it and the
        /// client waits for the one after.
        /// </summary>
        public async Task PollAsync()
        {
            await Wait.UntilAsync(() => Time.WaitsOnly(PollInterval));
            Time.Advance(PollInterval);
            await Wait.UntilAsync(() => Client.State.LastContactUtc == Time.GetUtcNow() && Time.WaitsOnly(PollInterval));
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await _task.WaitAsync(TestContext.Current.CancellationToken);
            _cts.Dispose();
            Client.Dispose();
        }
    }

    /// <summary>A web server that answers every request with the same body and 200.</summary>
    private sealed class CannedDevice : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        private CannedDevice(TcpListener listener, byte[] body, bool contentLength)
        {
            _listener = listener;
            _loop = ServeAsync(body, contentLength);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static CannedDevice Start(byte[] body, bool contentLength)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new CannedDevice(listener, body, contentLength);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _loop;
            _listener.Dispose();
            _stop.Dispose();
        }

        private async Task ServeAsync(byte[] body, bool contentLength)
        {
            try
            {
                while (true)
                {
                    using var tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = tcp.GetStream();
                    var buffer = new byte[4096];
                    _ = await stream.ReadAsync(buffer, _stop.Token);
                    var header = $"HTTP/1.1 200 OK\r\n{(contentLength ? $"Content-Length: {body.Length}\r\n" : "")}Connection: close\r\n\r\n";
                    try
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _stop.Token);
                        await stream.WriteAsync(body, _stop.Token);
                    }
                    catch (IOException)
                    {
                        // The client stopped reading a huge answer, as it should.
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException
                || (ex is InvalidOperationException && _stop.IsCancellationRequested))
            {
                // Stopped.
            }
        }
    }
}
