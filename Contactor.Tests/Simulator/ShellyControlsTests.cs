using Contactor.Boards;
using Contactor.Boards.Shelly;
using Contactor.Simulator;
using Contactor.Tests.Web;

namespace Contactor.Tests.Simulator;

/// <summary>The simulator page's controls for a Shelly's special cases, on a device that isn't started.</summary>
public sealed class ShellyControlsTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 18, 0, 0, TimeSpan.Zero);

    private readonly ShellySimulator _device = new(ShellyModels.TwoRelays);
    private readonly RunningBoard _board;

    public ShellyControlsTests()
    {
        _board = new RunningBoard(1, _device, null, fromSettings: false, new FixedTimeProvider(Now));
    }

    private ShellyControls Controls => _board.Shelly!;

    public async ValueTask DisposeAsync()
    {
        await _board.DisposeAsync();
    }

    [Fact]
    public async Task Should_HaveNoShellyControls_ForAnotherBoard()
    {
        await using var board = new RunningBoard(2, new Contactor.Boards.Velleman.Vm201Simulator(), null, fromSettings: false, new FixedTimeProvider(Now));

        Assert.Null(board.Shelly);
        Assert.NotNull(_board.Shelly);
    }

    [Fact]
    public void Should_AskTheAppToWait_AndLogIt_OnlyWhenItChanges()
    {
        Controls.SetThrottle(true);
        Controls.SetThrottle(true);

        Assert.True(_device.Throttle);
        Assert.True(Controls.Throttle);
        Assert.Equal("Asking the app to wait (429) turned on here", Assert.Single(_board.Traffic).Text);

        Controls.SetThrottle(false);
        Assert.False(_device.Throttle);
        Assert.Equal("Asking the app to wait (429) turned off here", _board.Traffic[0].Text);
    }

    [Theory]
    [InlineData("1.6.0", "1.6.0")]
    [InlineData(" 1.7.1 ", "1.7.1")]
    [InlineData("2.0.0-beta1", "2.0.0-beta1")]
    [InlineData("abc", "abc")]
    public void Should_SetTheFirmware(string typed, string firmware)
    {
        Assert.Null(Controls.SetFirmware(typed));

        Assert.Equal(firmware, _device.Firmware);
        Assert.Equal($"Firmware set to {firmware} here", Assert.Single(_board.Traffic).Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.6.0 beta")]
    [InlineData("1.6\n0")]
    [InlineData("1.6.0\"")]
    [InlineData("ö.1")]
    [InlineData("123456789012345678901234567890123")]
    public void Should_RefuseAFirmware_ThePageDoesNotTake(string? typed)
    {
        Assert.Equal("The firmware is 1 to 32 letters, digits, '.', '-', '_' or '+'.", Controls.SetFirmware(typed));

        Assert.Equal("2.0.0", _device.Firmware);
        Assert.Empty(_board.Traffic);
    }

    [Fact]
    public void Should_KeepTheFirmware_WithoutALine_WhenItIsTheSame()
    {
        Assert.Null(Controls.SetFirmware("2.0.0"));

        Assert.Empty(_board.Traffic);
        Assert.Null(Controls.SetFirmware(new string('1', 32)));
        Assert.Equal(new string('1', 32), _device.Firmware);
    }

    [Fact]
    public void Should_SetTheProfile_AndIgnoreOneItDoesNotOffer()
    {
        Assert.Equal("", Controls.Profile);

        Controls.SetProfile("cover");
        Assert.Equal("cover", _device.Profile);
        Assert.Equal("Profile set to cover here", _board.Traffic[0].Text);

        Controls.SetProfile("");
        Assert.Null(_device.Profile);
        Assert.Equal("Profile set to none here", _board.Traffic[0].Text);

        var logged = _board.Traffic.Count;
        Controls.SetProfile("light");
        Controls.SetProfile(null);
        Controls.SetProfile("");
        Assert.Null(_device.Profile);
        Assert.Equal(logged, _board.Traffic.Count);
    }

    [Fact]
    public void Should_RefuseSwitchSet_OneRelayAtATime()
    {
        Controls.SetRefused(2, true);
        Controls.SetRefused(1, true);
        Controls.SetRefused(1, false);

        Assert.Equal(0b10, _device.FailRelays);
        Assert.Equal(ChannelMask.Of(2), Controls.Refused);
        Assert.Equal("Switch.Set for relay 1 carried out again here", _board.Traffic[0].Text);
        Assert.Equal("Switch.Set for relay 1 refused from now on here", _board.Traffic[1].Text);

        // What the page never offers changes nothing.
        var logged = _board.Traffic.Count;
        Controls.SetRefused(0, true);
        Controls.SetRefused(3, true);
        Controls.SetRefused(2, true);
        Assert.Equal(0b10, _device.FailRelays);
        Assert.Equal(logged, _board.Traffic.Count);
    }

    [Fact]
    public void Should_SetAutoOn_OneRelayAtATime()
    {
        Controls.SetAutoOn(2, true);
        Controls.SetAutoOn(2, true);

        Assert.True(_device.AutoOn(2));
        Assert.False(_device.AutoOn(1));
        Assert.Equal(ChannelMask.Of(2), Controls.AutoOn);
        Assert.Equal("Auto-on for relay 2 turned on here", Assert.Single(_board.Traffic).Text);

        Controls.SetAutoOn(0, true);
        Controls.SetAutoOn(3, true);
        Assert.Single(_board.Traffic);
    }

    [Fact]
    public void Should_SetTheStateAfterAPowerCut_OnlyToOneTheDocsList()
    {
        Assert.Equal(["off", "restore_last", "on", "match_input"], ShellyControls.InitialStates);

        Controls.SetInitialState(1, "restore_last");

        Assert.Equal("restore_last", _device.InitialState(1));
        Assert.Equal("off", _device.InitialState(2));
        Assert.Equal("Relay 1 set to restore_last after a power cut here", Assert.Single(_board.Traffic).Text);

        Controls.SetInitialState(1, "restore_last");
        Controls.SetInitialState(1, "toggle");
        Controls.SetInitialState(1, null);
        Controls.SetInitialState(3, "on");
        Assert.Equal("restore_last", _device.InitialState(1));
        Assert.Single(_board.Traffic);
    }

    [Fact]
    public void Should_AddASchedule_ThatSwitchesARelayEveryDay()
    {
        Assert.Null(Controls.AddSchedule(2, turnOn: true, "06:30"));
        Assert.Null(Controls.AddSchedule(1, turnOn: false, "00:05"));

        Assert.Equal(
            [
                new ShellySimulatedSchedule(1, true, "0 30 6 * * *", """[{"method":"Switch.Set","params":{"id":1,"on":true}}]"""),
                new ShellySimulatedSchedule(2, true, "0 5 0 * * *", """[{"method":"Switch.Set","params":{"id":0,"on":false}}]"""),
            ],
            Controls.Schedules);
        Assert.Equal(2, _device.ScheduleRevision);
        Assert.Equal("Schedule 2 added here: 00:05 every day · relay 1 off", _board.Traffic[0].Text);
        Assert.Equal("Schedule 1 added here: 06:30 every day · relay 2 on", _board.Traffic[1].Text);
    }

    [Theory]
    [InlineData(0, "08:00", "Choose a relay.")]
    [InlineData(3, "08:00", "Choose a relay.")]
    [InlineData(1, null, "The time is HH:mm, e.g. 08:00.")]
    [InlineData(1, "", "The time is HH:mm, e.g. 08:00.")]
    [InlineData(1, "8:00", "The time is HH:mm, e.g. 08:00.")]
    [InlineData(1, "24:00", "The time is HH:mm, e.g. 08:00.")]
    [InlineData(1, "08:60", "The time is HH:mm, e.g. 08:00.")]
    [InlineData(1, "08:00:00", "The time is HH:mm, e.g. 08:00.")]
    [InlineData(1, "-1:00", "The time is HH:mm, e.g. 08:00.")]
    public void Should_RefuseASchedule_ThePageDoesNotTake(int relay, string? time, string error)
    {
        Assert.Equal(error, Controls.AddSchedule(relay, turnOn: true, time));

        Assert.Empty(Controls.Schedules);
        Assert.Empty(_board.Traffic);
    }

    [Fact]
    public void Should_HoldAtMostTwentySchedules_AsTheDevice()
    {
        for (var i = 0; i < ShellyApi.MaxScheduleJobs; i++)
        {
            Assert.Null(Controls.AddSchedule(1, turnOn: true, "08:00"));
        }

        Assert.Equal("The device holds at most 20 schedules.", Controls.AddSchedule(1, turnOn: true, "08:00"));
        Assert.Equal(20, Controls.Schedules.Count);
    }

    [Fact]
    public void Should_TurnAScheduleOnAndOff_AndIgnoreOneTheDeviceDoesNotHold()
    {
        Assert.Null(Controls.AddSchedule(1, turnOn: true, "08:00"));

        Controls.SetScheduleEnabled(1, false);
        Controls.SetScheduleEnabled(1, false);

        Assert.False(_device.ScheduleEnabled(1));
        Assert.Equal(2, _device.ScheduleRevision);
        Assert.Equal("Schedule 1 turned off here", _board.Traffic[0].Text);

        var logged = _board.Traffic.Count;
        Controls.SetScheduleEnabled(2, true);
        Controls.SetScheduleEnabled(-1, true);
        Assert.Equal(logged, _board.Traffic.Count);
        Assert.Equal(2, _device.ScheduleRevision);
    }

    [Theory]
    [InlineData("0 0 8 * * *", """[{"method":"Switch.Set","params":{"id":0,"on":true}}]""", "08:00 every day · relay 1 on")]
    [InlineData("0 0 8 * * MON", """[{"method":"Switch.Set","params":{"id":3,"on":false}}]""", "0 0 8 * * MON · relay 4 off")]
    [InlineData("0 61 8 * * *", "[]", "0 61 8 * * * · no calls")]
    [InlineData("@sunrise", """[{"method":"Cover.Open","params":{"id":0}},{"method":"Switch.Set","params":{"id":"0","on":true}}]""", "@sunrise · Cover.Open, Switch.Set")]
    [InlineData("0 0 8 * * *", """[1,{"params":{}},{"method":""}]""", "08:00 every day · a call, a call, a call")]
    public void Should_DescribeASchedule_InWords(string timespec, string calls, string text)
    {
        Assert.Equal(text, ShellyControls.Describe(new ShellySimulatedSchedule(1, true, timespec, calls)));
    }

    [Fact]
    public void Should_CutALongMethodName_InTheDescription()
    {
        var calls = $$"""[{"method":"{{new string('x', 100)}}"}]""";

        Assert.Equal($"08:00 every day · {new string('x', 40)}…", ShellyControls.Describe(new ShellySimulatedSchedule(1, true, "0 0 8 * * *", calls)));
    }
}
