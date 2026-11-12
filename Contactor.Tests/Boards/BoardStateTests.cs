using Contactor.Boards;

namespace Contactor.Tests.Boards;

/// <summary>Whether the board has answered since the app started, which decides whether its states are known.</summary>
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
}
