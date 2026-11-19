using Contactor.Boards.Shelly;
using Contactor.Simulator;
using Contactor.Tests.Web;

namespace Contactor.Tests.Simulator;

/// <summary>The page's fields for a Shelly's controls, which belong to the board the page shows.</summary>
public sealed class ShellyFormTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 18, 0, 0, TimeSpan.Zero);

    private readonly RunningBoard _board = new(1, new ShellySimulator(ShellyModels.TwoRelays), null, fromSettings: false, new FixedTimeProvider(Now));

    public async ValueTask DisposeAsync()
    {
        await _board.DisposeAsync();
    }

    [Fact]
    public void Should_AddAnOffSchedule_WhenOffIsChosen()
    {
        var form = new ShellyForm { ScheduleRelay = 2, ScheduleOn = ShellyForm.Off, ScheduleTime = "21:15" };

        form.AddSchedule(_board.Shelly!);

        Assert.Null(form.ScheduleError);
        Assert.Equal("21:15 every day · relay 2 off", ShellyControls.Describe(Assert.Single(_board.Shelly!.Schedules)));

        form.ScheduleOn = ShellyForm.On;
        form.AddSchedule(_board.Shelly!);
        Assert.Equal("21:15 every day · relay 2 on", ShellyControls.Describe(_board.Shelly!.Schedules[1]));
    }

    [Fact]
    public void Should_TakeOnlyTheSelectsOwnValues()
    {
        var form = new ShellyForm();

        form.SetScheduleOn(ShellyForm.Off);
        form.SetScheduleRelay("2");
        Assert.Equal((ShellyForm.Off, 2), (form.ScheduleOn, form.ScheduleRelay));

        form.SetScheduleOn("true");
        form.SetScheduleOn(null);
        form.SetScheduleRelay("-1");
        form.SetScheduleRelay("x");
        form.SetScheduleRelay(null);
        form.SetScheduleRelay("99999999999");
        Assert.Equal((ShellyForm.Off, 2), (form.ScheduleOn, form.ScheduleRelay));
    }

    [Fact]
    public void Should_ShowAnError_UntilTheNextTry()
    {
        var form = new ShellyForm { ScheduleTime = "25:00" };
        form.SetFirmware(_board.Shelly!, "");
        form.AddSchedule(_board.Shelly!);

        Assert.NotNull(form.FirmwareError);
        Assert.Equal("The time is HH:mm, e.g. 08:00.", form.ScheduleError);

        form.SetFirmware(_board.Shelly!, "1.7.0");
        form.ScheduleTime = "07:00";
        form.AddSchedule(_board.Shelly!);
        Assert.Null(form.FirmwareError);
        Assert.Null(form.ScheduleError);
    }

    [Fact]
    public void Should_StartAfresh_WhenAnotherBoardIsShown()
    {
        var form = new ShellyForm();
        form.Follow(1);
        form.SetFirmware(_board.Shelly!, "");
        form.ScheduleRelay = 2;
        form.ScheduleOn = ShellyForm.Off;
        form.ScheduleTime = "25:00";
        form.AddSchedule(_board.Shelly!);

        // The same board again keeps everything.
        form.Follow(1);
        Assert.NotNull(form.FirmwareError);
        Assert.NotNull(form.ScheduleError);
        Assert.Equal(2, form.ScheduleRelay);

        // Another board, or none after the shown one was removed: all afresh.
        foreach (int? next in new int?[] { 2, null })
        {
            form.Follow(next);
            Assert.Equal(next, form.BoardId);
            Assert.Null(form.FirmwareError);
            Assert.Null(form.ScheduleError);
            Assert.Equal(1, form.ScheduleRelay);
            Assert.Equal(ShellyForm.On, form.ScheduleOn);
            Assert.Equal(ShellyForm.DefaultTime, form.ScheduleTime);
            form.ScheduleRelay = 2;
        }
    }
}
