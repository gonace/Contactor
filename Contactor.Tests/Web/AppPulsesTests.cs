using Contactor.Boards;
using Contactor.Web.Features.Relays;

namespace Contactor.Tests.Web;

/// <summary>
/// The app's own pulses on a board: how long a relay's ON and OFF stay locked, that pages are told when the lock may
/// have ended, and that a pulse and a press of the same relay are decided in one step.
/// </summary>
public sealed class AppPulsesTests
{
    private BoardState _state = new(true, ChannelMask.None, ChannelMask.None, null);

    // Until the length has passed whatever the board shows; then only while the board still shows the relay on, for
    // at most PulseOffWait (a polled board shows the end a poll or two later).
    [Fact]
    public void Should_LockARelay_UntilItsPulseHasEnded_AndTheBoardShowsItOff()
    {
        var time = new ManualTimeProvider();
        using var pulses = new AppPulses(time, () => _state);
        var changes = 0;
        Show(2, 5);

        Assert.NotNull(pulses.TryStart(ChannelMask.Of([2, 5]), TimeSpan.FromSeconds(2), () => changes++));

        Show();
        Assert.True(pulses.IsPulsing(2));
        Show(2, 5);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, changes);
        Assert.True(pulses.IsPulsing(2));
        time.Advance(RelayControl.PulseOffWait);
        Assert.False(pulses.IsPulsing(2));
        Assert.Equal(2, changes);
        Assert.Equal(0, time.PendingTimers);
    }

    // Once the board has shown the relay off after the pulse's length, the pulse is over for good: a person's ON after
    // that is never taken for the pulse, also within PulseOffWait.
    [Fact]
    public void Should_EndThePulseForGood_OnceTheBoardShowsTheRelayOff()
    {
        var time = new ManualTimeProvider();
        using var pulses = new AppPulses(time, () => _state);
        Show(3, 4);
        pulses.TryStart(ChannelMask.Of([3, 4]), TimeSpan.FromSeconds(2), () => { });

        time.Advance(TimeSpan.FromSeconds(2));
        Show(4);
        pulses.Observe(_state);
        time.Advance(TimeSpan.FromSeconds(3));
        Show(3, 4);

        Assert.False(pulses.IsPulsing(3));
        Assert.True(pulses.IsPulsing(4));
        using (var hold = pulses.TryHold(ChannelMask.Of(3)))
        {
            Assert.NotNull(hold);
        }

        // At the end itself: a relay the board already showed off (e.g. after "All off") is free at once.
        Show();
        Assert.NotNull(pulses.TryStart(ChannelMask.Of(3), TimeSpan.FromSeconds(2), () => { }));
        time.Advance(TimeSpan.FromSeconds(2));
        Show(3);
        Assert.False(pulses.IsPulsing(3));
    }

    // Checked and started, or checked and held, in one step: a second pulse or a press of a pulsing relay is refused, a
    // pulse doesn't start while a press is on its way, and a pulse that didn't go out unlocks only itself.
    [Fact]
    public void Should_DecideAPulseAndAPress_InOneStep()
    {
        var time = new ManualTimeProvider();
        using var pulses = new AppPulses(time, () => _state);

        var first = pulses.TryStart(ChannelMask.Of(1), TimeSpan.FromSeconds(2), () => { });
        Assert.NotNull(first);
        Assert.Null(pulses.TryStart(ChannelMask.Of(1), TimeSpan.FromSeconds(2), () => { }));
        Assert.Null(pulses.TryHold(ChannelMask.Of(1)));

        var press = pulses.TryHold(ChannelMask.Of(2));
        Assert.Null(pulses.TryStart(ChannelMask.Of(2), TimeSpan.FromSeconds(2), () => { }));
        press!.Dispose();
        var second = pulses.TryStart(ChannelMask.Of(2), TimeSpan.FromSeconds(2), () => { });
        Assert.NotNull(second);

        pulses.Forget(second!);
        Assert.False(pulses.IsPulsing(2));
        Assert.True(pulses.IsPulsing(1));
        pulses.Clear();
        Assert.False(pulses.IsPulsing(1));
        Assert.Equal(0, time.PendingTimers);
    }

    private void Show(params int[] on)
    {
        _state = _state with { Relays = ChannelMask.Of(on) };
    }
}
