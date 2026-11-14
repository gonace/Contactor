using Contactor.Boards;
using Contactor.Tests.Web;

namespace Contactor.Tests.Boards;

/// <summary>The pulses a simulated board runs, ended on its own clock.</summary>
public sealed class SimulatedPulsesTests
{
    [Fact]
    public void Should_EndAPulseAfterItsTime_WithAllItsRelaysAtOnce()
    {
        var time = new ManualTimeProvider();
        using var pulses = new SimulatedPulses(time);
        List<ulong> ended = [];

        pulses.Start(0b0110, TimeSpan.FromSeconds(2), take => ended.Add(take()));
        time.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.Empty(ended);
        Assert.Equal(0b0110UL, pulses.Running);
        time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal([0b0110UL], ended);
        Assert.Equal(0UL, pulses.Running);
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public void Should_StartARelayAgain_InALaterPulse()
    {
        var time = new ManualTimeProvider();
        using var pulses = new SimulatedPulses(time);
        List<(string, ulong)> ended = [];

        pulses.Start(0b0011, TimeSpan.FromSeconds(10), take => ended.Add(("first", take())));
        time.Advance(TimeSpan.FromSeconds(5));
        pulses.Start(0b0001, TimeSpan.FromSeconds(10), take => ended.Add(("second", take())));
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal([("first", 0b0010UL)], ended);
        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal([("first", 0b0010UL), ("second", 0b0001UL)], ended);
    }

    [Fact]
    public void Should_EndNothing_ForACancelledRelay()
    {
        var time = new ManualTimeProvider();
        using var pulses = new SimulatedPulses(time);
        List<ulong> ended = [];
        pulses.Start(0b0011, TimeSpan.FromSeconds(1), take => ended.Add(take()));
        pulses.Start(0b0100, TimeSpan.FromSeconds(1), take => ended.Add(take()));

        pulses.Cancel(0b0101);
        Assert.Equal(1, time.PendingTimers);
        pulses.Cancel(0b0010);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(ended);
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public void Should_TakeNothing_WhenACommandEndedThePulse_AfterItsTimerFired()
    {
        // The board's command comes between the timer and the board's lock: the end must not switch the relay back.
        var time = new ManualTimeProvider();
        using var pulses = new SimulatedPulses(time);
        List<ulong> taken = [];
        pulses.Start(0b0011, TimeSpan.FromSeconds(1), take =>
        {
            pulses.Cancel(0b0001);
            taken.Add(take());
            taken.Add(take());
        });

        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal([0b0010UL, 0UL], taken);
        Assert.Equal(0UL, pulses.Running);
    }

    [Fact]
    public void Should_TakeNothing_WhenANewPulseReplacedItsRelays_AfterItsTimerFired()
    {
        var time = new ManualTimeProvider();
        using var pulses = new SimulatedPulses(time);
        List<(string, ulong)> taken = [];
        pulses.Start(0b0001, TimeSpan.FromSeconds(1), take =>
        {
            pulses.Start(0b0001, TimeSpan.FromSeconds(5), next => taken.Add(("second", next())));
            taken.Add(("first", take()));
        });

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([("first", 0UL)], taken);
        Assert.Equal(0b0001UL, pulses.Running);
        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal([("first", 0UL), ("second", 0b0001UL)], taken);
    }

    [Fact]
    public void Should_EndNothing_OnceDisposed()
    {
        var time = new ManualTimeProvider();
        var pulses = new SimulatedPulses(time);
        List<ulong> ended = [];
        pulses.Start(0b0001, TimeSpan.FromSeconds(1), take => ended.Add(take()));

        pulses.Dispose();
        pulses.Start(0b0010, TimeSpan.FromSeconds(1), take => ended.Add(take()));
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(ended);
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public void Should_RefuseALengthOfNothing_AndIgnoreNoRelays()
    {
        var time = new ManualTimeProvider();
        using var pulses = new SimulatedPulses(time);

        Assert.Throws<ArgumentOutOfRangeException>(() => pulses.Start(1, TimeSpan.Zero, _ => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => pulses.Start(1, TimeSpan.FromSeconds(-1), _ => { }));
        pulses.Start(0, TimeSpan.FromSeconds(1), _ => { });

        Assert.Equal(0, time.PendingTimers);
    }
}
