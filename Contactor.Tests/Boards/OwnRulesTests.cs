using Contactor.Boards;

namespace Contactor.Tests.Boards;

/// <summary>A board's own rules, which switch its relays without the app, and what the app can know about them.</summary>
public sealed class OwnRulesTests
{
    [Fact]
    public void Should_BeEqual_WhenTheyHoldTheSameRulesInTheSameOrder()
    {
        var rules = new OwnRules([new OwnRule(3, OwnRuleKind.Timer), new OwnRule(1, OwnRuleKind.InputToggle)]);
        var same = new OwnRules([new OwnRule(3, OwnRuleKind.Timer), new OwnRule(1, OwnRuleKind.InputToggle)]);

        Assert.Equal(rules, same);
        Assert.True(rules == same);
        Assert.Equal(rules.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(rules, new OwnRules([new OwnRule(1, OwnRuleKind.InputToggle), new OwnRule(3, OwnRuleKind.Timer)]));
        Assert.NotEqual(rules, new OwnRules([new OwnRule(3, OwnRuleKind.Timer), new OwnRule(1, OwnRuleKind.InputEdge)]));
        Assert.True(rules != OwnRules.Empty);
        Assert.False(rules.Equals(null));
        Assert.Equal(OwnRules.Empty, new OwnRules([]));
        Assert.Equal("[Timer 3, InputToggle 1]", rules.ToString());
    }

    // A state with the same rules is the same state, so a board can tell when they changed.
    [Fact]
    public void Should_MakeStatesEqual_WhenTheirRulesAre()
    {
        var state = BoardState.Initial with { OwnRules = new OwnRules([new OwnRule(2, OwnRuleKind.Timer)]) };

        Assert.Equal(state, BoardState.Initial with { OwnRules = new OwnRules([new OwnRule(2, OwnRuleKind.Timer)]) });
        Assert.NotEqual(state, BoardState.Initial with { OwnRules = OwnRules.Empty });
        Assert.NotEqual(BoardState.Initial with { OwnRules = OwnRules.Empty }, BoardState.Initial);
        Assert.Null(BoardState.Initial.OwnRules);
    }

    [Fact]
    public void Should_KnowTheRelaysTheRulesSwitch_AndTheRulesOfEachRelay()
    {
        var rules = new OwnRules([new OwnRule(3, OwnRuleKind.Timer), new OwnRule(1, OwnRuleKind.InputLinkage), new OwnRule(3, OwnRuleKind.InputEdge)]);

        Assert.Equal(ChannelMask.Of([1, 3]), rules.Relays);
        Assert.Equal([new OwnRule(3, OwnRuleKind.Timer), new OwnRule(3, OwnRuleKind.InputEdge)], rules.For(3));
        Assert.Equal([new OwnRule(1, OwnRuleKind.InputLinkage)], rules.For(1));
        Assert.Empty(rules.For(2));
        Assert.Empty(rules.For(0));
        Assert.Empty(rules.For(65));
        Assert.Equal(ChannelMask.None, OwnRules.Empty.Relays);
        Assert.Equal(3, rules.Count);
        Assert.Equal(new OwnRule(1, OwnRuleKind.InputLinkage), rules[1]);
    }

    [Fact]
    public void Should_RefuseARuleForNoRelay_OrOfNoKind()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OwnRule(0, OwnRuleKind.Timer));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OwnRule(ChannelMask.MaxChannels + 1, OwnRuleKind.Timer));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OwnRule(1, (OwnRuleKind)99));
        Assert.Equal(ChannelMask.MaxChannels, new OwnRule(ChannelMask.MaxChannels, OwnRuleKind.Timer).Relay);
        Assert.Throws<ArgumentNullException>(() => new OwnRules(null!));
        Assert.Throws<ArgumentException>(() => new OwnRules([null!]));
    }

    // A board that says nothing is never taken to have none.
    [Fact]
    public void Should_TakeABoardThatDoesntSay_AsOneThatCantTell()
    {
        Assert.Same(OwnRulesAbility.CantTell, new BoardCapabilities(8, 0, ReportsChanges: false).OwnRulesOrCantTell);
        Assert.Same(OwnRulesAbility.None, new BoardCapabilities(8, 0, ReportsChanges: false, OwnRules: OwnRulesAbility.None).OwnRulesOrCantTell);
        Assert.Equal((false, false, false), (OwnRulesAbility.None.Reads, OwnRulesAbility.None.TurnsOff, OwnRulesAbility.None.MayHaveUnseen));
        Assert.Equal((false, false, true), (OwnRulesAbility.CantTell.Reads, OwnRulesAbility.CantTell.TurnsOff, OwnRulesAbility.CantTell.MayHaveUnseen));
        Assert.Equal((true, true, false), (OwnRulesAbility.ReadsAndTurnsOff.Reads, OwnRulesAbility.ReadsAndTurnsOff.TurnsOff, OwnRulesAbility.ReadsAndTurnsOff.MayHaveUnseen));
        Assert.Throws<ArgumentException>(() => new OwnRulesAbility(Reads: false, TurnsOff: true, MayHaveUnseen: false));
    }

    // Only a board that turns its rules off does: the others say so before anything is sent.
    [Fact]
    public async Task Should_RefuseToTurnOffRules_OnABoardThatCant()
    {
        IRelayBoard board = new PlainBoard();

        await Assert.ThrowsAsync<NotSupportedException>(() => board.TurnOffOwnRulesAsync([new OwnRule(1, OwnRuleKind.Timer)], TestContext.Current.CancellationToken));
    }

    // A board that implements only what every board must.
    private sealed class PlainBoard : IRelayBoard
    {
        public BoardCapabilities Capabilities { get; } = new(1, 0, ReportsChanges: false);

        public BoardState State => BoardState.Initial;

        public event EventHandler<BoardState>? StateChanged
        {
            add { }
            remove { }
        }

        public Task<ChannelMask> TurnOnAsync(ChannelMask relays, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ChannelMask> TurnOffAsync(ChannelMask relays, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ChannelMask> PulseAsync(ChannelMask relays, TimeSpan length, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RefreshAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
