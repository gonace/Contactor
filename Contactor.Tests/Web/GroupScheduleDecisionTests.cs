using Contactor.Web.Features.Schedules;

namespace Contactor.Tests.Web;

/// <summary>What a group's schedules want at a tick, without a board: the same rules as a relay's, plus retries.</summary>
public sealed class GroupScheduleDecisionTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 13, 11, 0, DateTimeKind.Utc);
    private static readonly DateTime From = Now.AddMinutes(-1);

    // A run 14:00–15:00 UTC, and one that ended at the last tick (13:10, the OFF that failed).
    private static readonly ScheduleWindow Running = new(1, null, 1, Now.AddMinutes(-11), Now.AddHours(1));
    private static readonly ScheduleWindow StartsNow = new(1, null, 1, Now, Now.AddHours(1));
    private static readonly ScheduleWindow EndedNow = new(1, null, 1, Now.AddHours(-1), Now);
    private static readonly ScheduleWindow EndedLong = new(1, null, 1, Now.AddHours(-2), Now.AddMinutes(-30));

    [Fact]
    public void Should_StartAtOnTime_ButNotARunThatWasAlreadyGoing()
    {
        Assert.True(Decide([StartsNow], isOn: false));
        Assert.Null(Decide([Running], isOn: false));
        Assert.Null(Decide([StartsNow], isOn: true));
    }

    [Fact]
    public void Should_StartARunningRun_OnReconcileOrARetriedStart()
    {
        Assert.True(Decide([Running], isOn: false, reconcile: true));
        Assert.True(Decide([Running], isOn: false, retry: true));
        Assert.Null(Decide([], isOn: false, retry: true));
    }

    [Fact]
    public void Should_StopAtALiveOff_WhoeverStartedIt()
    {
        Assert.False(Decide([EndedNow], isOn: true));
        Assert.Null(Decide([EndedNow], isOn: false));
    }

    [Fact]
    public void Should_StopAPersonsStart_OnlyAtALiveOffOrARetriedStop()
    {
        Assert.Null(Decide([EndedLong], isOn: true));
        Assert.Null(Decide([], isOn: true));

        // The OFF at the last tick failed; that run is no longer in this tick's windows.
        Assert.False(Decide([], isOn: true, retry: false));
    }

    [Fact]
    public void Should_StopWhatTheScheduleStarted_WhenNoRunCoversIt()
    {
        Assert.False(Decide([], isOn: true, turnedOnBySchedule: true));
        Assert.False(Decide([EndedLong], isOn: true, turnedOnBySchedule: true));
        Assert.Null(Decide([Running], isOn: true, turnedOnBySchedule: true));
    }

    [Fact]
    public void Should_LetOnWin_WhenRunsOverlap()
    {
        Assert.Null(Decide([EndedNow, Running], isOn: true));
    }

    private static bool? Decide(
        ScheduleWindow[] windows,
        bool isOn,
        bool turnedOnBySchedule = false,
        bool? retry = null,
        bool reconcile = false)
    {
        return SchedulerService.Decide(windows, isOn, turnedOnBySchedule, retry, From, Now, reconcile);
    }
}
