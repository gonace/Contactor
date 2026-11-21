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

    // Schedule A switches relays 1 and 2, schedule B relays 2 and 3, and relay 4 has an auto-on of its own.
    private static readonly OwnRule T1 = new(1, OwnRuleKind.Timer);
    private static readonly OwnRule T2 = new(2, OwnRuleKind.Timer);
    private static readonly OwnRule T3 = new(3, OwnRuleKind.Timer);
    private static readonly OwnRule A4 = new(4, OwnRuleKind.AutoOn);

    private static OwnRules TwoSchedules()
    {
        return new OwnRules([T1, T2, T3, A4], [[T1, T2], [T3, T2]]);
    }

    // Turning off relay 1's timer turns off A, but B still makes relay 2's: nothing else goes. Turning off 1 and 3
    // together turns off both, and relay 2's goes with them.
    [Fact]
    public void Should_KnowWhichRulesGoWithATurnOff_FromTheUnitsBehindThem()
    {
        var rules = TwoSchedules();

        Assert.Empty(rules.GoesWith(T1));
        Assert.Empty(rules.GoesWith(T3));
        Assert.Equal([T1, T3], rules.GoesWith(T2));
        Assert.Equal([T2], rules.GoesWith([T1, T3]));
        Assert.Equal([T2], rules.GoesWith([T3, T1, T1, A4]));
        Assert.Empty(rules.GoesWith(A4));
        Assert.Empty(rules.GoesWith([T1, T2, T3]));
        Assert.Empty(rules.GoesWith([]));
        Assert.Empty(rules.GoesWith(new OwnRule(5, OwnRuleKind.Timer)));
        Assert.Equal([T2], new OwnRules([T1, T2], [[T1, T2]]).GoesWith(T1));
        Assert.Empty(new OwnRules([T1, T2]).GoesWith([T1]));
        Assert.Empty(OwnRules.Empty.GoesWith(T1));
        Assert.Equal([[T1, T2], [T2, T3]], rules.Units.Select(u => u.ToList()));
        Assert.Empty(new OwnRules([T1]).Units);
        Assert.Equal("[Timer 1, Timer 2, Timer 3, AutoOn 4; (Timer 1, Timer 2); (Timer 2, Timer 3)]", rules.ToString());
    }

    // Two boards' rules are the same only with the same units behind them: a change in those alone is a change.
    [Fact]
    public void Should_BeEqual_OnlyWithTheSameUnits()
    {
        var rules = TwoSchedules();

        Assert.Equal(rules, new OwnRules([T1, T2, T3, A4], [[T2, T3], [T2, T1], [T1, T2], []]));
        Assert.Equal(rules.GetHashCode(), new OwnRules([T1, T2, T3, A4], [[T2, T3], [T2, T1]]).GetHashCode());
        Assert.NotEqual(rules, new OwnRules([T1, T2, T3, A4]));
        Assert.NotEqual(rules, new OwnRules([T1, T2, T3, A4], [[T1, T2]]));
        Assert.NotEqual(rules, new OwnRules([T1, T2, T3, A4], [[T1, T2], [T3]]));
        Assert.Equal(new OwnRules([T1, T2]), new OwnRules([T1, T2], [[]]));
        Assert.NotEqual(BoardState.Initial with { OwnRules = rules }, BoardState.Initial with { OwnRules = new OwnRules([T1, T2, T3, A4]) });
    }

    // A rule the app can't turn off (e.g. a timer running on the VM208) never goes with another, and a change in which
    // rules those are alone is a change.
    [Fact]
    public void Should_KnowTheRulesTheAppCantTurnOff()
    {
        var rules = new OwnRules([T1, T2, T3], [[T1, T2], [T3]], [T2, T2]);

        Assert.Equal([T2], rules.CantTurnOff);
        Assert.False(rules.CanTurnOff(T2));
        Assert.True(rules.CanTurnOff(T1));
        Assert.False(rules.CanTurnOff(null!));
        Assert.Empty(rules.GoesWith(T1));
        Assert.Empty(new OwnRules([T1]).CantTurnOff);
        Assert.Equal(rules, new OwnRules([T1, T2, T3], [[T1, T2], [T3]], [T2]));
        Assert.NotEqual(rules, new OwnRules([T1, T2, T3], [[T1, T2], [T3]]));
        Assert.Equal("[Timer 1, Timer 2, Timer 3; (Timer 1, Timer 2); (Timer 3); can't turn off: Timer 2]", rules.ToString());
        Assert.Throws<ArgumentException>(() => new OwnRules([T1], null, [T2]));
        Assert.Throws<ArgumentException>(() => new OwnRules([T1], null, [null!]));
    }

    [Fact]
    public void Should_RefuseAUnit_WithARuleTheBoardDoesntHave()
    {
        Assert.Throws<ArgumentException>(() => new OwnRules([T1], [[T1, T2]]));
        Assert.Throws<ArgumentException>(() => new OwnRules([T1], [null!]));
        Assert.Throws<ArgumentException>(() => new OwnRules([T1], [[null!]]));
        Assert.Throws<ArgumentNullException>(() => new OwnRules([T1]).GoesWith((IEnumerable<OwnRule>)null!));
        Assert.Empty(new OwnRules([T1]).GoesWith((OwnRule)null!));
    }

    // The board in memory turns off every unit behind the rules asked, and with them what only those made; it returns
    // only the rules asked and keeps the units left.
    [Fact]
    public async Task Should_TurnOffTheRulesThatGoWithThem_OnTheBoardInMemory()
    {
        var board = new FakeRelayBoard(ownRules: OwnRulesAbility.ReadsAndTurnsOff);
        board.SetOwnRules(new OwnRules([T1, T2, T3, A4], [[T1, T2], [T3, T2], [T3, A4]]));

        Assert.Equal([T1], await board.TurnOffOwnRulesAsync([T1], TestContext.Current.CancellationToken));
        Assert.Equal(new OwnRules([T2, T3, A4], [[T3, T2], [T3, A4]]), board.State.OwnRules);

        Assert.Equal([T3], await board.TurnOffOwnRulesAsync([T3], TestContext.Current.CancellationToken));
        Assert.Equal(OwnRules.Empty, board.State.OwnRules);
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
