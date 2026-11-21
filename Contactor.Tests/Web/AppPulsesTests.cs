using Contactor.Boards;
using Contactor.Web.Features.Relays;

namespace Contactor.Tests.Web;

/// <summary>
/// The app's own pulses on a board: how long a relay's ON and OFF stay locked, and that pages are told when the lock may
/// have ended.
/// </summary>
public sealed class AppPulsesTests
{
    // Until the length has passed whatever the board shows; then while the board still shows the relay on, for at most
    // PulseOffWait (a polled board shows the end a poll or two later).
    [Fact]
    public void Should_LockARelay_UntilItsPulseHasEnded_AndTheBoardShowsItOff()
    {
        var time = new ManualTimeProvider();
        using var pulses = new AppPulses(time);
        var changes = 0;

        pulses.Start(ChannelMask.Of([2, 5]), TimeSpan.FromSeconds(2), () => changes++);

        Assert.True(pulses.IsPulsing(2, shownOn: false));
        Assert.Equal(ChannelMask.Of(5), pulses.Pulsing(ChannelMask.Of([1, 5]), BoardState.Initial));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(2, changes);
        Assert.False(pulses.IsPulsing(2, shownOn: false));
        Assert.True(pulses.IsPulsing(2, shownOn: true));
        time.Advance(RelayControl.PulseOffWait);
        Assert.False(pulses.IsPulsing(2, shownOn: true));
        Assert.Equal(4, changes);
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public void Should_Unlock_APulseThatWasForgotten_OrAMovedBoard()
    {
        var time = new ManualTimeProvider();
        using var pulses = new AppPulses(time);

        pulses.Start(ChannelMask.Of([1, 2]), TimeSpan.FromSeconds(2), () => { });
        pulses.Forget(ChannelMask.Of(1));
        Assert.False(pulses.IsPulsing(1, shownOn: true));
        Assert.True(pulses.IsPulsing(2, shownOn: true));
        pulses.Clear();
        Assert.False(pulses.IsPulsing(2, shownOn: true));
        Assert.Equal(0, time.PendingTimers);
    }
}
