using System.Net.Sockets;
using System.Text;
using Contactor.Boards;
using Contactor.Boards.Velleman;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Velleman;

/// <summary>
/// The VM208's own rules: its relays' week schedules, read with <c>GET /getalarms</c> and turned off with
/// <c>POST /shedule_set</c>, as Velleman's firmware (https://github.com/Velleman/VM208) takes them; the simulator
/// answers as the firmware does. The client's clock is a <see cref="ManualTimeProvider"/>.
/// </summary>
public sealed class Vm208ScheduleTests
{
    private const string StatusRequest = "GET /status?interface=-1&socket=0";

    // As server.on("/getalarms") writes it with ArduinoJson 5 for a relay as the board ships (data/alarms/0.json): the
    // "on" alarms Sunday first, then the "off" alarms, every one disabled.
    private const string FactoryAlarms =
        "{\"Channel\":{\"alarms\":["
        + "{\"dow\":0,\"hour\":13,\"minute\":28,\"onoff\":true,\"enabled\":false},"
        + "{\"dow\":1,\"hour\":13,\"minute\":28,\"onoff\":true,\"enabled\":false},"
        + "{\"dow\":2,\"hour\":13,\"minute\":28,\"onoff\":true,\"enabled\":false},"
        + "{\"dow\":3,\"hour\":13,\"minute\":28,\"onoff\":true,\"enabled\":false},"
        + "{\"dow\":4,\"hour\":13,\"minute\":28,\"onoff\":true,\"enabled\":false},"
        + "{\"dow\":5,\"hour\":13,\"minute\":28,\"onoff\":true,\"enabled\":false},"
        + "{\"dow\":6,\"hour\":13,\"minute\":28,\"onoff\":true,\"enabled\":false},"
        + "{\"dow\":0,\"hour\":13,\"minute\":29,\"onoff\":false,\"enabled\":false},"
        + "{\"dow\":1,\"hour\":13,\"minute\":29,\"onoff\":false,\"enabled\":false},"
        + "{\"dow\":2,\"hour\":13,\"minute\":29,\"onoff\":false,\"enabled\":false},"
        + "{\"dow\":3,\"hour\":13,\"minute\":29,\"onoff\":false,\"enabled\":false},"
        + "{\"dow\":4,\"hour\":13,\"minute\":29,\"onoff\":false,\"enabled\":false},"
        + "{\"dow\":5,\"hour\":13,\"minute\":29,\"onoff\":false,\"enabled\":false},"
        + "{\"dow\":6,\"hour\":13,\"minute\":29,\"onoff\":false,\"enabled\":false}]}}";

    private static readonly string[] ScheduleReads = ["GET /getalarms?channel=1", "GET /getalarms?channel=2", "GET /getalarms?channel=3", "GET /getalarms?channel=4"];

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Monday to Friday on at 06:30 and off at 08:00, the weekend off but at its own times.
    private static IReadOnlyList<Vm208Alarm> Weekdays { get; } =
    [
        .. Enumerable.Range(0, 7).Select(d => new Vm208Alarm(d, 6, 30, true, d is >= 1 and <= 5)),
        .. Enumerable.Range(0, 7).Select(d => new Vm208Alarm(d, 8, 0, false, d is >= 1 and <= 5)),
    ];

    [Fact]
    public void Should_ReadTheSchedule_AsTheFirmwareWritesIt()
    {
        var alarms = Vm208Json.ReadAlarms(Encoding.UTF8.GetBytes(FactoryAlarms));

        Assert.Equal(Vm208Simulator.FactorySchedule, alarms);
        Assert.Equal(FactoryAlarms, Vm208Json.WriteAlarms(alarms));
        Assert.Equal(new Vm208Alarm(1, 8, 0, false, true), Vm208Json.ReadAlarms(Encoding.UTF8.GetBytes(Vm208Json.WriteAlarms(Weekdays)))[8]);
    }

    [Theory]
    [InlineData("{\"Channel\":{\"alarms\":[]}}")]
    [InlineData("{\"Channels\":[]}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("\"dow\":0,", "\"dow\":1,")]
    [InlineData("\"onoff\":true,\"enabled\":false},{\"dow\":1", "\"onoff\":false,\"enabled\":false},{\"dow\":1")]
    [InlineData("\"hour\":13,\"minute\":28", "\"hour\":24,\"minute\":28")]
    [InlineData("\"hour\":13,\"minute\":28", "\"hour\":13,\"minute\":60")]
    [InlineData("\"hour\":13,\"minute\":28", "\"hour\":-1,\"minute\":28")]
    [InlineData("\"hour\":13,\"minute\":28", "\"hour\":1.5,\"minute\":28")]
    [InlineData("\"enabled\":false}", "\"enabled\":1}")]
    [InlineData(",\"enabled\":false}", "}")]
    public void Should_RefuseAScheduleTheFirmwareDoesntWrite(string broken, string? replacement = null)
    {
        var json = replacement is null ? broken : ReplaceFirst(FactoryAlarms, broken, replacement);

        Assert.Throws<Vm208ProtocolException>(() => Vm208Json.ReadAlarms(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Should_RefuseAnAnswerLongerThanTheLimit()
    {
        var huge = Encoding.UTF8.GetBytes(FactoryAlarms + new string(' ', Vm208Api.MaxResponseBytes));

        Assert.Throws<Vm208ProtocolException>(() => Vm208Json.ReadAlarms(huge));
    }

    // Pairs 1–7 are the "on" alarms Monday to Sunday, 8–14 the "off" alarms, as data/www/js/shedule.js sends them and
    // server.on("/shedule_set") reads them by position.
    [Fact]
    public void Should_WriteTheScheduleForm_InTheFirmwaresOrder()
    {
        var form = Vm208Api.ScheduleForm(3, [.. Weekdays.Select(a => a with { Enabled = false })]);

        Assert.Equal(
            "relay=3"
            + "&alarm_time_1=06%3A30&alarm_enable_1=false&alarm_time_2=06%3A30&alarm_enable_2=false&alarm_time_3=06%3A30&alarm_enable_3=false"
            + "&alarm_time_4=06%3A30&alarm_enable_4=false&alarm_time_5=06%3A30&alarm_enable_5=false&alarm_time_6=06%3A30&alarm_enable_6=false"
            + "&alarm_time_7=06%3A30&alarm_enable_7=false"
            + "&alarm_time_8=08%3A00&alarm_enable_8=false&alarm_time_9=08%3A00&alarm_enable_9=false&alarm_time_10=08%3A00&alarm_enable_10=false"
            + "&alarm_time_11=08%3A00&alarm_enable_11=false&alarm_time_12=08%3A00&alarm_enable_12=false&alarm_time_13=08%3A00&alarm_enable_13=false"
            + "&alarm_time_14=08%3A00&alarm_enable_14=false",
            form);
        Assert.Equal(29, form.Split('&').Length);

        // Read back as the firmware reads it: each alarm in its place.
        var read = Vm208TraceText.ReadScheduleForm(Vm208Api.ScheduleForm(1, Weekdays));
        Assert.Equal(1L, read!.Value.Relay);
        Assert.Equal(Weekdays, read.Value.Alarms);
        Assert.Equal("alarm_enable_1=true", Vm208Api.ScheduleForm(1, Weekdays).Split('&')[2]);
        Assert.Equal("alarm_enable_7=false", Vm208Api.ScheduleForm(1, Weekdays).Split('&')[14]);
    }

    // Channel 0 crashes the board, and a channel above 4 is an extension module's: the app's helpers refuse both.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5)]
    public void Should_NeverMakeARequestForARelayTheCardHasNot(int relay)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Vm208Api.AlarmsQuery(relay));
        Assert.Throws<ArgumentOutOfRangeException>(() => Vm208Api.ScheduleForm(relay, Weekdays));
    }

    [Fact]
    public void Should_RefuseToWriteAScheduleTheFirmwareCantTake()
    {
        Assert.Throws<ArgumentException>(() => Vm208Api.ScheduleForm(1, [.. Weekdays.Take(13)]));
        Assert.Throws<ArgumentException>(() => Vm208Api.ScheduleForm(1, [.. Weekdays.Reverse()]));
        Assert.Throws<ArgumentException>(() => Vm208Api.ScheduleForm(1, [Weekdays[0] with { Hour = 24 }, .. Weekdays.Skip(1)]));
    }

    [Fact]
    public void Should_SayWhatAScheduleRequestDoes()
    {
        Assert.Equal("Ask for relay 2's schedule", Vm208TraceText.Request("GET", Vm208Api.AlarmsPath, null, "channel=2"));
        Assert.Equal("Ask for channel 0's schedule", Vm208TraceText.Request("GET", Vm208Api.AlarmsPath, null, "channel=0"));
        Assert.Equal("Turn off relay 3's schedule", Vm208TraceText.Request("POST", Vm208Api.ScheduleSetPath, Vm208Api.ScheduleForm(3, Vm208Simulator.FactorySchedule)));
        Assert.Equal("Save relay 1's schedule", Vm208TraceText.Request("POST", Vm208Api.ScheduleSetPath, Vm208Api.ScheduleForm(1, Weekdays)));
        Assert.Equal("Relay 1's schedule: 10 of 14 alarms on", Vm208TraceText.Schedule(1, Weekdays));
        Assert.Equal("Relay 4's schedule: all alarms off", Vm208TraceText.Schedule(4, Vm208Simulator.FactorySchedule));
    }

    [Fact]
    public async Task Should_AnswerTheSchedules_AsTheFirmware()
    {
        await using var board = new Vm208Simulator();
        board.Start();
        await board.SetAlarmsAsync(2, Weekdays);

        Assert.EndsWith(FactoryAlarms, await SendAsync(board.Port, "GET /getalarms?channel=1"), StringComparison.Ordinal);
        Assert.EndsWith(Vm208Json.WriteAlarms(Weekdays), await SendAsync(board.Port, "GET /getalarms?channel=2"), StringComparison.Ordinal);

        // The firmware reads the channel by position into 16 bits: 65538 is 2.
        Assert.EndsWith(Vm208Json.WriteAlarms(Weekdays), await SendAsync(board.Port, "GET /getalarms?x=65538"), StringComparison.Ordinal);

        // Saved, and read back as saved.
        var saved = await SendAsync(board.Port, "POST /shedule_set", Vm208Api.ScheduleForm(2, [.. Weekdays.Select(a => a with { Enabled = false })]));
        Assert.Equal("200", Code(saved));
        Assert.Equal([.. Weekdays.Select(a => a with { Enabled = false })], board.Alarms(2));
        Assert.Equal(ChannelMask.None, board.Timers);

        // Any other number of parameters is refused, and changes nothing.
        Assert.Equal("400", Code(await SendAsync(board.Port, "POST /shedule_set", "relay=1&alarm_time_1=06%3A30")));
        Assert.Equal("400", Code(await SendAsync(board.Port, "POST /shedule_set", Vm208Api.ScheduleForm(1, Weekdays) + "&x=1")));
        Assert.Equal(Vm208Simulator.FactorySchedule, board.Alarms(1));
        Assert.Equal(0, board.UnansweredCount);
    }

    // The firmware takes a time as given into its uint8_t fields (99:00 stays 99:00, -1 is 255), saves it and answers
    // it so (src/Network/server_VM208.cpp lines 231–281 and 522–572); the board never runs such an alarm. The
    // simulator does the same and keeps answering; the client reads such a schedule as unreadable, never as none.
    [Fact]
    public async Task Should_KeepATimeOutOfRange_AsTheFirmware_AndKeepAnswering()
    {
        await using var board = new Vm208Simulator();
        board.Start();
        var form = ReplaceFirst(ReplaceFirst(Vm208Api.ScheduleForm(1, Weekdays), "alarm_time_1=06%3A30", "alarm_time_1=99%3A00"), "alarm_time_2=06%3A30", "alarm_time_2=-1%3A30");

        Assert.Equal("200", Code(await SendAsync(board.Port, "POST /shedule_set", form)));
        Assert.Equal(new Vm208Alarm(1, 99, 0, true, true), board.Alarms(1)[1]);
        Assert.Equal(new Vm208Alarm(2, 255, 30, true, true), board.Alarms(1)[2]);

        var answer = await SendAsync(board.Port, "GET /getalarms?channel=1");
        Assert.Equal("200", Code(answer));
        Assert.Contains("{\"dow\":1,\"hour\":99,\"minute\":0,\"onoff\":true,\"enabled\":true}", answer, StringComparison.Ordinal);
        Assert.Contains("\"hour\":255,", answer, StringComparison.Ordinal);
        Assert.Equal("200", Code(await SendAsync(board.Port, StatusRequest)));
        Assert.Equal(0, board.UnansweredCount);

        await using var run = Run.Start(board.Port, pollSeconds: 30);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        Assert.Null(run.Client.State.OwnRules);
    }

    // Channel 0 and a channel from 269 on crash the real board; 5–268 are an extension module's, not simulated.
    [Theory]
    [InlineData("GET /getalarms", null)]
    [InlineData("GET /getalarms?channel=0", null)]
    [InlineData("GET /getalarms?channel=5", null)]
    [InlineData("GET /getalarms?channel=269", null)]
    [InlineData("GET /getalarms?channel=x", null)]
    [InlineData("POST /shedule_set", "relay=0")]
    [InlineData("POST /shedule_set", "relay=5")]
    public async Task Should_LeaveUnanswered_TheScheduleRequestsTheFirmwareWouldCrashOn(string request, string? relay)
    {
        await using var board = new Vm208Simulator();
        board.Start();
        var form = relay is null ? null : relay + Vm208Api.ScheduleForm(1, Weekdays)["relay=1".Length..];

        Assert.Equal("", await SendAsync(board.Port, request, form));

        Assert.Equal(1, board.UnansweredCount);
        Assert.Equal(ChannelMask.None, board.Timers);
    }

    [Fact]
    public async Task Should_SetTheTimers_AsTheBoardsPageWould()
    {
        await using var board = new Vm208Simulator();
        board.Start();
        var timers = board;
        Assert.IsAssignableFrom<ISimulatedBoardTimers>(board);
        var changes = 0;
        board.Changed += (_, _) => Interlocked.Increment(ref changes);

        await timers.SetTimerAsync(3, turnOn: true);
        await timers.SetTimersAsync(ChannelMask.Of([1, 3]));
        await timers.SetTimerAsync(3, turnOn: false);

        Assert.Equal(ChannelMask.Of(1), timers.Timers);
        Assert.All(board.Alarms(1), a => Assert.True(a.Enabled));
        Assert.Equal(3, changes);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => timers.SetTimerAsync(5, turnOn: true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => timers.SetTimersAsync(ChannelMask.Of(5)));
        await Assert.ThrowsAsync<ArgumentException>(() => board.SetAlarmsAsync(1, [.. Weekdays.Take(3)]));
    }

    [Fact]
    public async Task Should_ReadTheSchedules_WhenItConnects()
    {
        await using var simulator = new Vm208Simulator();
        simulator.Start();
        await simulator.SetAlarmsAsync(2, Weekdays);
        await simulator.SetAlarmsAsync(4, [.. Vm208Simulator.FactorySchedule.Select((a, i) => a with { Enabled = i == 13 })]);
        await using var run = Run.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);

        OwnRule[] rules = [new(2, OwnRuleKind.Timer), new(4, OwnRuleKind.Timer)];
        Assert.Equal(new OwnRules(rules, rules.Select(r => (IReadOnlyCollection<OwnRule>)[r])), run.Client.State.OwnRules);
        Assert.Equal([StatusRequest, .. ScheduleReads], simulator.ReceivedRequests);

        // Each relay's schedule is its own: turning one off takes no other rule with it.
        Assert.Empty(run.Client.State.OwnRules!.GoesWith(rules[0]));
        Assert.Contains(run.Traces, t => t.Text == "Relay 2's schedule: 10 of 14 alarms on");
    }

    [Fact]
    public async Task Should_ReadTheSchedulesAgain_OnlyEveryInterval_AndTellAChangeOfThemAlone()
    {
        await using var simulator = new Vm208Simulator();
        simulator.Start();
        await using var run = Run.Start(simulator.Port, pollSeconds: 20);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        Assert.Equal(OwnRules.Empty, run.Client.State.OwnRules);
        var changes = run.Changes.Count;

        await simulator.SetAlarmsAsync(1, Weekdays);

        // Two polls within the interval read the status only.
        await run.PollAsync(simulator, requests: 1);
        await run.PollAsync(simulator, requests: 1);
        Assert.Equal(OwnRules.Empty, run.Client.State.OwnRules);

        // The third, a minute after the first read, reads them again.
        await run.PollAsync(simulator, requests: 1 + Vm208Api.RelayCount);
        Assert.Equal<OwnRule>([new OwnRule(1, OwnRuleKind.Timer)], run.Client.State.OwnRules);
        Assert.Equal(1, run.RuleChanges);

        // Not a state change: the safety services aren't woken by it.
        Assert.Equal(changes, run.Changes.Count);
        Assert.Equal(1 + (3 * 1) + (2 * Vm208Api.RelayCount), simulator.RequestCount);
    }

    // The schedules are read before the board's first connected state, so unknown rules while it answers mean they
    // couldn't be read, which the screens say (issue #551); a refused read is in the test below.
    [Fact]
    public async Task Should_ReadTheSchedules_BeforeTheFirstConnectedState()
    {
        await using var simulator = new Vm208Simulator();
        simulator.Start();
        await simulator.SetAlarmsAsync(2, Weekdays);
        await using var run = Run.Start(simulator.Port);

        await Wait.UntilAsync(() => run.Changes.Any(s => s.IsConnected));

        Assert.Equal<OwnRule>([new OwnRule(2, OwnRuleKind.Timer)], run.Changes.First(s => s.IsConnected).OwnRules);
    }

    // A schedule the app can't read never loses the board: the rules are unknown until a read works.
    [Fact]
    public async Task Should_StayConnected_WhenTheSchedulesCantBeRead()
    {
        await using var board = FakeBoard.Start();
        await using var run = Run.Start(board.Port, pollSeconds: 30);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        Assert.Null(run.Client.State.OwnRules);
        Assert.Contains(run.Traces, t => t.IsError && t.Text.StartsWith("The board's schedules can't be read: ", StringComparison.Ordinal));

        // The read stopped at the first relay, and isn't tried again before the interval.
        Assert.Equal(1, board.ScheduleRequests);
        run.Time.Advance(run.PollInterval);
        await Wait.UntilAsync(() => board.StatusRequests == 2 && run.Time.WaitsOnly(run.PollInterval));
        Assert.Equal(1, board.ScheduleRequests);

        board.Fixed = true;
        run.Time.Advance(run.PollInterval);
        await Wait.UntilAsync(() => board.StatusRequests == 3 && run.Time.WaitsOnly(run.PollInterval));
        Assert.Equal<OwnRule>([new OwnRule(1, OwnRuleKind.Timer)], run.Client.State.OwnRules);
        Assert.Equal(1, run.RuleChanges);
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_TurnOffARelaysSchedule_WithOneSave_AndReadItBack()
    {
        await using var simulator = new Vm208Simulator();
        simulator.Start();
        await simulator.SetAlarmsAsync(2, Weekdays);
        await simulator.SetAlarmsAsync(3, Weekdays);
        await using var run = Run.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        var before = simulator.ReceivedRequests.Count;

        var taken = await run.Client.TurnOffOwnRulesAsync([new OwnRule(2, OwnRuleKind.Timer), new OwnRule(2, OwnRuleKind.Timer)], Cancel);

        Assert.Equal<OwnRule>([new OwnRule(2, OwnRuleKind.Timer)], taken);

        // Read fresh, saved with each alarm at its own time and disabled, read back, then the status.
        var off = Vm208Api.ScheduleForm(2, [.. Weekdays.Select(a => a with { Enabled = false })]);
        Assert.Equal(
            ["GET /getalarms?channel=2", $"POST /shedule_set {off}", "GET /getalarms?channel=2", StatusRequest],
            simulator.ReceivedRequests.Skip(before));
        Assert.Equal([.. Weekdays.Select(a => a with { Enabled = false })], simulator.Alarms(2));
        Assert.Equal(Weekdays, simulator.Alarms(3));

        // The next state confirms it, with no poll in between.
        Assert.Equal<OwnRule>([new OwnRule(3, OwnRuleKind.Timer)], run.Client.State.OwnRules);
        Assert.Equal(1, run.RuleChanges);

        // The board's settings, which it answers, never show.
        Assert.Contains(run.Traces, t => t.Text == "The board saved relay 2's schedule" && t.Bytes == "200 (the board's settings, not shown)");
        Assert.DoesNotContain(run.Traces, t => (t.Bytes ?? "").Contains("USERNAME", StringComparison.Ordinal));
        AssertOnlySafeRequests(simulator);
    }

    // A save the board refuses takes nothing, and the board stays connected: it answered.
    [Fact]
    public async Task Should_TakeNothing_WhenTheBoardRefusesTheSave()
    {
        await using var board = FakeBoard.Start();
        board.Fixed = true;
        board.SaveCode = 400;
        await using var run = Run.Start(board.Port, pollSeconds: 30);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        Assert.Equal<OwnRule>([new OwnRule(1, OwnRuleKind.Timer)], run.Client.State.OwnRules);

        var refused = await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.Timer)], Cancel));

        Assert.Contains("answered 400", refused.Message, StringComparison.Ordinal);
        Assert.Equal(1, board.Saves);
        Assert.True(run.Client.State.IsConnected);
        Assert.Equal<OwnRule>([new OwnRule(1, OwnRuleKind.Timer)], run.Client.State.OwnRules);
    }

    // Relays 1 and 2 have schedules; relay 1's save is refused (400) or answered too long to read (-1), relay 2's is
    // taken. The other relay still goes, and only what was taken comes back.
    [Theory]
    [InlineData(400)]
    [InlineData(-1)]
    public async Task Should_ReturnOnlyWhatWasTaken_WhenOneRelaysSaveFails(int firstSave)
    {
        await using var board = FakeBoard.Start();
        board.Fixed = true;
        board.Timers.Add(2);
        board.SaveCodes[1] = firstSave;
        await using var run = Run.Start(board.Port, pollSeconds: 30);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        Assert.Equal<OwnRule>([new OwnRule(1, OwnRuleKind.Timer), new OwnRule(2, OwnRuleKind.Timer)], run.Client.State.OwnRules);

        var taken = await run.Client.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.Timer), new OwnRule(2, OwnRuleKind.Timer)], Cancel);

        Assert.Equal<OwnRule>([new OwnRule(2, OwnRuleKind.Timer)], taken);
        Assert.Equal(2, board.Saves);
        Assert.True(run.Client.State.IsConnected);
        if (firstSave == 400)
        {
            // Refused: the board kept relay 1's schedule, and the read back of relay 2's confirms it.
            Assert.Equal<OwnRule>([new OwnRule(1, OwnRuleKind.Timer)], run.Client.State.OwnRules);
        }
        else
        {
            // An answer that can't be read may hide a save (this board made it): relay 1's rule stays as last read
            // until the next poll, which reads every schedule again, not a minute later.
            Assert.Equal<OwnRule>([new OwnRule(1, OwnRuleKind.Timer)], run.Client.State.OwnRules);
            await Wait.UntilAsync(() => run.Time.WaitsOnly(run.PollInterval));
            var reads = board.ScheduleRequests;
            run.Time.Advance(run.PollInterval);
            await Wait.UntilAsync(() => run.Client.State.OwnRules is { Count: 0 } && run.Time.WaitsOnly(run.PollInterval));
            Assert.Equal(reads + Vm208Api.RelayCount, board.ScheduleRequests);
        }
    }

    // The board answered 200 to the save, so the rule counts as taken; only the next state confirms it. A read back
    // that still shows enabled alarms keeps the rule, and one that fails makes the rules unknown, never gone.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_CountASaveAsTaken_ButLeaveTheRuleToTheReadBack(bool readBackFails)
    {
        await using var board = FakeBoard.Start();
        board.Fixed = true;
        board.IgnoreSaves = !readBackFails;
        board.BreakAfterSave = readBackFails;
        await using var run = Run.Start(board.Port, pollSeconds: 30);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));

        var taken = await run.Client.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.Timer)], Cancel);

        Assert.Equal<OwnRule>([new OwnRule(1, OwnRuleKind.Timer)], taken);
        Assert.Equal(1, board.Saves);
        Assert.True(run.Client.State.IsConnected);
        if (readBackFails)
        {
            Assert.Null(run.Client.State.OwnRules);
        }
        else
        {
            Assert.Equal<OwnRule>([new OwnRule(1, OwnRuleKind.Timer)], run.Client.State.OwnRules);
        }
    }

    // A firmware without /getalarms answers 404 every minute: the first failure toasts, the next ones are routine (in
    // the Debug list, no toast) until a read works again.
    [Fact]
    public async Task Should_ToastAFailedScheduleRead_OnlyOnce_UntilAReadWorks()
    {
        await using var board = FakeBoard.Start();
        board.BrokenCode = 404;
        await using var run = Run.Start(board.Port, pollSeconds: 30);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        var toasts = Toasts(run);
        Assert.NotEmpty(toasts);
        Assert.Contains(toasts, t => t.Text == "The board answered 404");
        Assert.Contains(toasts, t => t.Text.StartsWith("The board's schedules can't be read: ", StringComparison.Ordinal));

        // Two more failed reads, a minute apart: in the trace, but no toast.
        for (var reads = 2; reads <= 3; reads++)
        {
            await NextScheduleReadAsync(run, board, reads);
        }

        Assert.Equal(toasts.Count, Toasts(run).Count);
        Assert.Contains(run.Traces, t => t.IsError && t.IsRoutine && t.Text == "The board answered 404");

        // A read that works, then a failure again: that one toasts.
        board.Fixed = true;
        await NextScheduleReadAsync(run, board, 3 + Vm208Api.RelayCount);
        var afterFixed = Toasts(run).Count;
        board.Fixed = false;
        await NextScheduleReadAsync(run, board, 4 + Vm208Api.RelayCount);
        Assert.True(Toasts(run).Count > afterFixed);
        Assert.Null(run.Client.State.OwnRules);
        Assert.True(run.Client.State.IsConnected);
    }

    // A save without an answer may have been made: the next poll reads every schedule again, not a minute later.
    [Fact]
    public async Task Should_ReadTheSchedulesAgain_AfterASaveWithoutAnAnswer()
    {
        await using var board = FakeBoard.Start();
        board.Fixed = true;
        board.SaveCode = 0;
        await using var run = Run.Start(board.Port, pollSeconds: 30);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        var reads = board.ScheduleRequests;

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => run.Client.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.Timer)], Cancel));

        // The poller reads the board at once after a failed command, schedules included.
        await Wait.UntilAsync(() => board.ScheduleRequests == reads + 1 + Vm208Api.RelayCount && run.Time.WaitsOnly(run.PollInterval));
        Assert.Equal(1, board.Saves);
        Assert.True(run.Client.State.IsConnected);
    }

    [Fact]
    public async Task Should_RefuseWhatItCantTurnOff_BeforeSendingAnything()
    {
        await using var simulator = new Vm208Simulator();
        simulator.Start();
        await using var run = Run.Start(simulator.Port);
        await Wait.UntilAsync(() => run.Client.State.IsConnected);
        var sent = simulator.ReceivedRequests.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.TurnOffOwnRulesAsync([new OwnRule(5, OwnRuleKind.Timer)], Cancel));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.Client.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.AutoOn)], Cancel));
        await Assert.ThrowsAsync<ArgumentException>(() => run.Client.TurnOffOwnRulesAsync([null!], Cancel));
        Assert.Empty(await run.Client.TurnOffOwnRulesAsync([], Cancel));

        Assert.Equal(sent, simulator.ReceivedRequests.Count);
    }

    [Fact]
    public async Task Should_SendNothing_WithoutAConnection()
    {
        await using var simulator = new Vm208Simulator();
        simulator.Start();
        simulator.RejectConnections = true;
        using var client = CreateClient(simulator.Port, new ManualTimeProvider());

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => client.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.Timer)], Cancel));

        Assert.Empty(simulator.ReceivedRequests);
    }

    // Whatever the client does, it never asks for channel 0 or a channel above 4, never sends /setalarm, which the board
    // forgets at a restart, and never /stoptimer or /stoppulse, which toggle the relay.
    [Fact]
    public async Task Should_NeverSendARequestThatCrashesOrTogglesTheBoard()
    {
        await using var simulator = new Vm208Simulator();
        simulator.Start();
        for (var relay = 1; relay <= Vm208Api.RelayCount; relay++)
        {
            await simulator.SetAlarmsAsync(relay, Weekdays);
        }

        await using var run = Run.Start(simulator.Port, pollSeconds: 30);
        await Wait.UntilAsync(() => run.Client.State.IsConnected && run.Time.WaitsOnly(run.PollInterval));
        await run.Client.TurnOnAsync(0b1111, Cancel);
        await run.Client.TurnOffOwnRulesAsync([.. Enumerable.Range(1, 4).Select(r => new OwnRule(r, OwnRuleKind.Timer))], Cancel);
        await run.Client.TurnOffAsync(0b1111, Cancel);
        await run.PollAsync(simulator, requests: 1);
        await run.PollAsync(simulator, requests: 1 + Vm208Api.RelayCount);

        Assert.Equal(OwnRules.Empty, run.Client.State.OwnRules);
        Assert.Equal(4, simulator.ReceivedRequests.Count(r => r.StartsWith("POST /shedule_set ", StringComparison.Ordinal)));
        AssertOnlySafeRequests(simulator);
        Assert.Equal(0, simulator.UnansweredCount);
    }

    [Fact]
    public async Task Should_PassTheRulesOn_AsTheBoard()
    {
        await using var simulator = new Vm208Simulator();
        simulator.Start();
        await simulator.SetAlarmsAsync(1, Weekdays);
        var time = new ManualTimeProvider();
        using var client = CreateClient(simulator.Port, time);
        var board = new Vm208Board(client, NullLogger<Vm208Board>.Instance);
        var ruleChanges = 0;
        board.OwnRulesChanged += (_, _) => throw new InvalidOperationException("A broken subscriber.");
        board.OwnRulesChanged += (_, _) => Interlocked.Increment(ref ruleChanges);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var task = client.RunAsync(stop.Token);
        await Wait.UntilAsync(() => board.State.IsConnected);

        Assert.Equal(new OwnRulesAbility(Reads: true, TurnsOff: true, MayHaveUnseen: true), board.Capabilities.OwnRules);
        Assert.Equal<OwnRule>([new OwnRule(1, OwnRuleKind.Timer)], board.State.OwnRules);
        Assert.Equal<OwnRule>([new OwnRule(1, OwnRuleKind.Timer)], await board.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.Timer)], Cancel));
        Assert.Equal(OwnRules.Empty, board.State.OwnRules);
        Assert.Equal(1, Volatile.Read(ref ruleChanges));

        await stop.CancelAsync();
        await task;
    }

    private static void AssertOnlySafeRequests(Vm208Simulator simulator)
    {
        Assert.All(simulator.ReceivedRequests, r =>
        {
            Assert.DoesNotContain("/setalarm", r, StringComparison.Ordinal);
            Assert.DoesNotContain("/stoptimer", r, StringComparison.Ordinal);
            Assert.DoesNotContain("/stoppulse", r, StringComparison.Ordinal);
            Assert.DoesNotContain("/getalarms?channel=0", r, StringComparison.Ordinal);
            Assert.DoesNotContain("relay=0&", r, StringComparison.Ordinal);
            if (r.StartsWith("GET /getalarms", StringComparison.Ordinal))
            {
                Assert.Contains(r, ScheduleReads);
            }

            if (r.StartsWith("POST /shedule_set ", StringComparison.Ordinal))
            {
                Assert.Matches("^POST /shedule_set relay=[1-4]&", r);
            }
        });
    }

    private static List<BoardTraceEntry> Toasts(Run run)
    {
        return [.. run.Traces.Where(t => t.IsError && !t.IsRoutine)];
    }

    // Two polls on: the schedules are read once a minute.
    private static async Task NextScheduleReadAsync(Run run, FakeBoard board, int scheduleRequests)
    {
        for (var poll = 0; poll < 2; poll++)
        {
            var statuses = board.StatusRequests;
            run.Time.Advance(run.PollInterval);
            await Wait.UntilAsync(() => board.StatusRequests == statuses + 1 && run.Time.WaitsOnly(run.PollInterval));
        }

        Assert.Equal(scheduleRequests, board.ScheduleRequests);
    }

    private static string ReplaceFirst(string text, string old, string replacement)
    {
        var at = text.IndexOf(old, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{old}' isn't in the text.");
        return text[..at] + replacement + text[(at + old.Length)..];
    }

    private static Vm208Client CreateClient(int port, TimeProvider time, int pollSeconds = 2)
    {
        var options = new Vm208Options { Host = "127.0.0.1", Port = port, PollSeconds = pollSeconds };
        return new Vm208Client(Options.Create(options), time, NullLogger<Vm208Client>.Instance);
    }

    private static string Code(string response)
    {
        return response.Split(' ')[1];
    }

    // The whole answer, or "" when the board closed the connection without one.
    private static async Task<string> SendAsync(int port, string requestLine, string? form = null)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port, Cancel);
        var stream = tcp.GetStream();
        var body = form is null ? "" : $"Content-Type: application/x-www-form-urlencoded\r\nContent-Length: {form.Length}\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"{requestLine} HTTP/1.1\r\nHost: board\r\n{body}\r\n{form}"), Cancel);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(Cancel);
        }
        catch (IOException)
        {
            return "";
        }
    }

    private sealed class Run : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _task;
        private readonly List<BoardTraceEntry> _traces = [];
        private readonly List<Vm208State> _changes = [];
        private int _ruleChanges;

        private Run(Vm208Client client, ManualTimeProvider time, TimeSpan pollInterval)
        {
            Client = client;
            Time = time;
            PollInterval = pollInterval;
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
            client.OwnRulesChanged += (_, _) => Interlocked.Increment(ref _ruleChanges);
            _task = client.RunAsync(_cts.Token);
        }

        public Vm208Client Client { get; }

        public ManualTimeProvider Time { get; }

        public TimeSpan PollInterval { get; }

        public int RuleChanges => Volatile.Read(ref _ruleChanges);

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

        public List<Vm208State> Changes
        {
            get
            {
                lock (_changes)
                {
                    return [.. _changes];
                }
            }
        }

        public static Run Start(int port, int pollSeconds = 2)
        {
            var time = new ManualTimeProvider();
            return new Run(CreateClient(port, time, pollSeconds), time, TimeSpan.FromSeconds(pollSeconds));
        }

        /// <summary>Moves the clock to the next poll, once the client waits for it, and waits until its requests were answered.</summary>
        public async Task PollAsync(Vm208Simulator simulator, int requests)
        {
            await Wait.UntilAsync(() => Time.WaitsOnly(PollInterval));
            var answered = simulator.RequestCount;
            Time.Advance(PollInterval);
            await Wait.UntilAsync(() => simulator.RequestCount == answered + requests && Time.WaitsOnly(PollInterval));
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await _task.WaitAsync(TestContext.Current.CancellationToken);
            _cts.Dispose();
            Client.Dispose();
        }
    }

    /// <summary>
    /// A board whose status is fine, whose schedules answer <see cref="BrokenCode"/> (500) until <see cref="Fixed"/> and
    /// then the relays in <see cref="Timers"/> (relay 1) have enabled alarms, and that answers a save with
    /// <see cref="SaveCode"/> (or <see cref="SaveCodes"/> for its relay), or closes the connection without an answer for 0,
    /// or answers 200 too long to read for -1. A save answered 200 turns the relay's alarms off, unless
    /// <see cref="IgnoreSaves"/>; after a save, the reads answer 500 while <see cref="BreakAfterSave"/>.
    /// </summary>
    private sealed class FakeBoard : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _statusRequests;
        private int _scheduleRequests;
        private int _saves;
        private bool _saved;

        private FakeBoard(TcpListener listener)
        {
            _listener = listener;
            _loop = ServeAsync();
        }

        public int Port => ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;

        public bool Fixed { get; set; }

        public int SaveCode { get; set; } = 200;

        public int BrokenCode { get; set; } = 500;

        public Dictionary<int, int> SaveCodes { get; } = [];

        public bool IgnoreSaves { get; set; }

        public bool BreakAfterSave { get; set; }

        public HashSet<int> Timers { get; } = [1];

        public int Saves => Volatile.Read(ref _saves);

        public int StatusRequests => Volatile.Read(ref _statusRequests);

        public int ScheduleRequests => Volatile.Read(ref _scheduleRequests);

        public static FakeBoard Start()
        {
            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            return new FakeBoard(listener);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // Stopped.
            }

            _listener.Dispose();
            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using var tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = tcp.GetStream();
                    var request = await ReadRequestAsync(stream);
                    if (request.StartsWith("POST /shedule_set", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref _saves);
                        var relay = int.Parse(request.Split("relay=", 2)[1].Split('&', 2)[0], System.Globalization.CultureInfo.InvariantCulture);
                        var code = SaveCodes.GetValueOrDefault(relay, SaveCode);
                        if (code == 200 || code == -1)
                        {
                            lock (Timers)
                            {
                                _saved = true;
                                if (!IgnoreSaves)
                                {
                                    Timers.Remove(relay);
                                }
                            }
                        }

                        if (code != 0)
                        {
                            var answer = code == -1 ? new string('x', Vm208Api.MaxResponseBytes + 1) : "";
                            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(code == -1 ? 200 : code)} X\r\nContent-Length: {answer.Length}\r\nConnection: close\r\n\r\n{answer}"), _stop.Token);
                        }

                        continue;
                    }

                    var isSchedule = request.StartsWith("GET /getalarms", StringComparison.Ordinal);
                    if (isSchedule)
                    {
                        Interlocked.Increment(ref _scheduleRequests);
                    }
                    else
                    {
                        Interlocked.Increment(ref _statusRequests);
                    }

                    var (status, body) = isSchedule ? Schedule(request) : (200, Vm208Json.WriteStatus(new Vm208Status(0, false, 0)));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), _stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException
                || (ex is InvalidOperationException && _stop.IsCancellationRequested))
            {
                // Stopped.
            }
        }

        // The request line and its form, once the whole body its Content-Length gives has come.
        private async Task<string> ReadRequestAsync(NetworkStream stream)
        {
            var buffer = new byte[8192];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), _stop.Token);
                if (read == 0)
                {
                    break;
                }

                length += read;
                var text = Encoding.ASCII.GetString(buffer, 0, length);
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end < 0)
                {
                    continue;
                }

                var header = text[..end].Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var bodyLength = header is null ? 0 : int.Parse(header["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                if (text.Length - end - 4 >= bodyLength)
                {
                    return $"{text.Split("\r\n", 2)[0]} {text[(end + 4)..]}";
                }
            }

            return Encoding.ASCII.GetString(buffer, 0, length);
        }

        private (int Code, string Body) Schedule(string request)
        {
            lock (Timers)
            {
                if (!Fixed || (BreakAfterSave && _saved))
                {
                    return (Fixed ? 500 : BrokenCode, "");
                }

                var relay = int.Parse(request.Split("channel=", 2)[1].Split(' ', 2)[0], System.Globalization.CultureInfo.InvariantCulture);
                return (200, Timers.Contains(relay) ? Vm208Json.WriteAlarms(Weekdays) : FactoryAlarms);
            }
        }
    }
}
