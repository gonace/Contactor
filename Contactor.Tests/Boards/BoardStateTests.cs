using Contactor.Boards;

namespace Contactor.Tests.Boards;

/// <summary>
/// Whether the board has answered since the app started, which decides whether its states are known, and which relays
/// it controls by itself.
/// </summary>
public sealed class BoardStateTests
{
    [Fact]
    public void Should_KnowWhetherTheBoardHasAnswered()
    {
        var answered = new BoardState(true, ChannelMask.Of(1), ChannelMask.None, DateTimeOffset.UnixEpoch);

        Assert.False(BoardState.Initial.HasAnswered);
        Assert.True(answered.HasAnswered);

        // A lost contact keeps the last known states: still answered.
        Assert.True((answered with { IsConnected = false }).HasAnswered);
    }

    // The relays kept unavailable count only while the rules are unknown (issue #567): known rules always win.
    [Fact]
    public void Should_KeepTheUnavailableRelays_OnlyWhileTheRulesAreUnknown()
    {
        var unknown = new BoardState(true, ChannelMask.None, ChannelMask.None, DateTimeOffset.UnixEpoch, KeptUnavailable: ChannelMask.Of(3));

        Assert.Equal(ChannelMask.Of(3), unknown.Unavailable);
        Assert.Equal(ChannelMask.None, (unknown with { OwnRules = OwnRules.Empty }).Unavailable);
        Assert.Equal(ChannelMask.Of(5), (unknown with { OwnRules = new OwnRules([new OwnRule(5, OwnRuleKind.InputLinkage)]) }).Unavailable);
    }
}
