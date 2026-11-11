using Contactor.Boards;
using Contactor.Web;
using Contactor.Web.Features.Events;

namespace Contactor.Tests.Web;

public sealed class ExpectedChangesTests
{
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
    private readonly ExpectedChanges _expected;

    public ExpectedChangesTests()
    {
        _expected = new ExpectedChanges(new LocalClock(TimeZoneInfo.Utc, _time));
    }

    [Fact]
    public void Should_MatchEachRelayInTheMaskOnce()
    {
        _expected.Expect(new ChannelMask(0b0000_0101), on: false);

        Assert.True(_expected.TryConsume(1, on: false, _time.GetUtcNow().UtcDateTime));
        Assert.True(_expected.TryConsume(3, on: false, _time.GetUtcNow().UtcDateTime));
        Assert.False(_expected.TryConsume(1, on: false, _time.GetUtcNow().UtcDateTime));
        Assert.False(_expected.TryConsume(2, on: false, _time.GetUtcNow().UtcDateTime));
    }

    [Fact]
    public void Should_NotMatchTheOppositeChange()
    {
        _expected.Expect(new ChannelMask(0b0000_0001), on: true);

        Assert.False(_expected.TryConsume(1, on: false, _time.GetUtcNow().UtcDateTime));
        Assert.True(_expected.TryConsume(1, on: true, _time.GetUtcNow().UtcDateTime));
    }

    [Fact]
    public void Should_ForgetAfterFiveSeconds()
    {
        _expected.Expect(new ChannelMask(0b0000_0001), on: true);

        _time.Now += ExpectedChanges.Lifetime - TimeSpan.FromMilliseconds(1);
        _expected.Expect(new ChannelMask(0b0000_0010), on: true);
        _time.Now += TimeSpan.FromMilliseconds(1);

        Assert.False(_expected.TryConsume(1, on: true, _time.GetUtcNow().UtcDateTime));
        Assert.True(_expected.TryConsume(2, on: true, _time.GetUtcNow().UtcDateTime));
    }

    [Fact]
    public void Should_Match_WhenTheStatusArrivedInTimeButIsHandledLater()
    {
        _expected.Expect(new ChannelMask(0b0000_0001), on: true);
        var received = _time.GetUtcNow().UtcDateTime + TimeSpan.FromSeconds(1);

        _time.Now += TimeSpan.FromMinutes(5);

        Assert.True(_expected.TryConsume(1, on: true, received));
    }

    [Fact]
    public void Should_KeepAnExpectation_ANewerCommandOutlives_UntilItsStatusIsHandled()
    {
        // Relay 3 on; its status arrives in time, but the recorder is behind. Auto-off then turns it off much later.
        _expected.Expect(new ChannelMask(0b0000_0100), on: true);
        var onArrived = _time.GetUtcNow().UtcDateTime + TimeSpan.FromSeconds(1);
        _time.Now += TimeSpan.FromHours(2);
        _expected.Expect(new ChannelMask(0b0000_0100), on: false);
        var offArrived = _time.GetUtcNow().UtcDateTime + TimeSpan.FromSeconds(1);

        Assert.True(_expected.TryConsume(3, on: true, onArrived));
        Assert.True(_expected.TryConsume(3, on: false, offArrived));
    }

    [Fact]
    public void Should_KeepOnlyTheNewest_WhenFarTooManyWait()
    {
        for (var i = 0; i < 300; i++)
        {
            _expected.Expect(new ChannelMask(0b0000_0001), on: i % 2 == 0);
        }

        _expected.Expect(new ChannelMask(0b0000_0010), on: true);

        Assert.True(_expected.TryConsume(2, on: true, _time.GetUtcNow().UtcDateTime));
    }

    [Fact]
    public void Should_NotMatch_WhenTheStatusArrivedTooLate()
    {
        _expected.Expect(new ChannelMask(0b0000_0001), on: true);

        Assert.False(_expected.TryConsume(1, on: true, _time.GetUtcNow().UtcDateTime + ExpectedChanges.Lifetime));
    }

    [Fact]
    public void Should_ExpectNothing_ForAnEmptyMask()
    {
        _expected.Expect(ChannelMask.None, on: false);

        for (var relayId = 1; relayId <= 8; relayId++)
        {
            Assert.False(_expected.TryConsume(relayId, on: false, _time.GetUtcNow().UtcDateTime));
        }
    }
}
