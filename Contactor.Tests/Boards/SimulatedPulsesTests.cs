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

        pulses.Start(0b0110, TimeSpan.FromSeconds(2), ended.Add);
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

        pulses.Start(0b0011, TimeSpan.FromSeconds(10), r => ended.Add(("first", r)));
        time.Advance(TimeSpan.FromSeconds(5));
        pulses.Start(0b0001, TimeSpan.FromSeconds(10), r => ended.Add(("second", r)));
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
        pulses.Start(0b0011, TimeSpan.FromSeconds(1), ended.Add);
        pulses.Start(0b0100, TimeSpan.FromSeconds(1), ended.Add);

        pulses.Cancel(0b0101);
        Assert.Equal(1, time.PendingTimers);
        pulses.Cancel(0b0010);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(ended);
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public void Should_EndNothing_OnceDisposed()
    {
        var time = new ManualTimeProvider();
        var pulses = new SimulatedPulses(time);
        List<ulong> ended = [];
        pulses.Start(0b0001, TimeSpan.FromSeconds(1), ended.Add);

        pulses.Dispose();
        pulses.Start(0b0010, TimeSpan.FromSeconds(1), ended.Add);
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
