using Contactor.Boards;

namespace Contactor.Tests.Boards;

/// <summary>A board's states after a power cut: the ability, the states it reports, and the interface's default.</summary>
public sealed class PowerOnTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_HoldEachStateOnce_InOrder()
    {
        var ability = new PowerOnAbility([PowerOnState.On, PowerOnState.Off, PowerOnState.On], perRelay: false);

        Assert.Equal([PowerOnState.Off, PowerOnState.On], ability.States);
        Assert.False(ability.PerRelay);
        Assert.True(ability.Has(PowerOnState.Off));
        Assert.False(ability.Has(PowerOnState.AsBefore));
        Assert.False(ability.Has((PowerOnState)4));
        Assert.Equal(ability, new PowerOnAbility([PowerOnState.Off, PowerOnState.On], perRelay: false));
        Assert.Equal(ability.GetHashCode(), new PowerOnAbility([PowerOnState.Off, PowerOnState.On], perRelay: false).GetHashCode());
        Assert.NotEqual(ability, new PowerOnAbility([PowerOnState.Off, PowerOnState.On], perRelay: true));
        Assert.NotEqual(ability, new PowerOnAbility([PowerOnState.Off], perRelay: false));
    }

    [Fact]
    public void Should_RefuseAnAbility_WithoutStatesOrWithOneThatIsntAState()
    {
        Assert.Throws<ArgumentNullException>(() => new PowerOnAbility(null!, perRelay: true));
        Assert.Throws<ArgumentException>(() => new PowerOnAbility([], perRelay: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PowerOnAbility([PowerOnState.Off, (PowerOnState)(-1)], perRelay: true));
    }

    [Fact]
    public void Should_LeaveThePowerOnAbility_ToTheModel()
    {
        var capabilities = new BoardCapabilities(8, 0, ReportsChanges: false);

        Assert.Null(capabilities.PowerOn);
        Assert.NotEqual(capabilities, capabilities with { PowerOn = new PowerOnAbility([PowerOnState.Off], perRelay: true) });
        Assert.Equal(
            capabilities with { PowerOn = new PowerOnAbility([PowerOnState.Off], perRelay: true) },
            capabilities with { PowerOn = new PowerOnAbility([PowerOnState.Off], perRelay: true) });
    }

    [Fact]
    public void Should_CompareTheReportedStates_ByValue()
    {
        var states = new PowerOnStates([PowerOnState.On, null, PowerOnState.FollowsInput]);

        Assert.Equal(3, states.Count);
        Assert.Equal(PowerOnState.On, states.For(1));
        Assert.Null(states.For(2));
        Assert.Equal(PowerOnState.FollowsInput, states.For(3));
        Assert.Null(states.For(0));
        Assert.Null(states.For(4));
        Assert.Equal(states, new PowerOnStates([PowerOnState.On, null, PowerOnState.FollowsInput]));
        Assert.True(states == new PowerOnStates([PowerOnState.On, null, PowerOnState.FollowsInput]));
        Assert.Equal(states.GetHashCode(), new PowerOnStates([PowerOnState.On, null, PowerOnState.FollowsInput]).GetHashCode());
        Assert.True(states != new PowerOnStates([PowerOnState.On, PowerOnState.Off, PowerOnState.FollowsInput]));
        Assert.NotEqual(states, new PowerOnStates([PowerOnState.On, null]));
        Assert.Equal("[On, unknown, FollowsInput]", states.ToString());
    }

    [Fact]
    public void Should_RefuseReportedStates_ForNoRelaysTooManyOrNotAState()
    {
        Assert.Throws<ArgumentNullException>(() => new PowerOnStates(null!));
        Assert.Throws<ArgumentException>(() => new PowerOnStates([]));
        Assert.Throws<ArgumentException>(() => new PowerOnStates(new PowerOnState?[ChannelMask.MaxChannels + 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PowerOnStates([PowerOnState.Off, (PowerOnState)7]));
        Assert.Equal(ChannelMask.MaxChannels, new PowerOnStates(new PowerOnState?[ChannelMask.MaxChannels]).Count);
    }

    [Fact]
    public void Should_CarryThePowerOnStates_InTheBoardsState()
    {
        var state = BoardState.Initial with { PowerOn = new PowerOnStates([PowerOnState.Off]) };

        Assert.Null(BoardState.Initial.PowerOn);
        Assert.Equal(state, BoardState.Initial with { PowerOn = new PowerOnStates([PowerOnState.Off]) });
        Assert.NotEqual(state, BoardState.Initial with { PowerOn = new PowerOnStates([PowerOnState.On]) });
        Assert.NotEqual(state, BoardState.Initial);
    }

    // A board that says nothing about it can't set it, and never raises its event.
    [Fact]
    public async Task Should_RefuseToSetThePowerOnState_ByDefault()
    {
        IRelayBoard board = new PlainBoard();
        EventHandler<BoardState> handler = (_, _) => { };
        board.PowerOnChanged += handler;
        board.PowerOnChanged -= handler;

        await Assert.ThrowsAsync<NotSupportedException>(() => board.SetPowerOnAsync(1, PowerOnState.Off, Cancel));
    }

    [Fact]
    public async Task Should_SetAndReport_OnTheFakeBoard()
    {
        var board = new FakeRelayBoard(relayCount: 2, powerOn: new PowerOnAbility([PowerOnState.Off, PowerOnState.On], perRelay: false));
        List<BoardState> reported = [];
        board.PowerOnChanged += (_, state) => reported.Add(state);

        await board.SetPowerOnAsync(2, PowerOnState.On, Cancel);

        // For the whole board: every relay.
        Assert.Equal(new PowerOnStates([PowerOnState.On, PowerOnState.On]), Assert.Single(reported).PowerOn);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.SetPowerOnAsync(1, PowerOnState.AsBefore, Cancel));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.SetPowerOnAsync(3, PowerOnState.Off, Cancel));
        await Assert.ThrowsAsync<NotSupportedException>(() => new FakeRelayBoard().SetPowerOnAsync(1, PowerOnState.Off, Cancel));
        board.SetConnected(false);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.SetPowerOnAsync(1, PowerOnState.Off, Cancel));
    }

    private sealed class PlainBoard : IRelayBoard
    {
        public event EventHandler<BoardState>? StateChanged
        {
            add { }
            remove { }
        }

        public BoardCapabilities Capabilities { get; } = new(1, 0, ReportsChanges: true);

        public BoardState State => BoardState.Initial;

        public Task<ChannelMask> TurnOnAsync(ChannelMask relays, CancellationToken cancellationToken = default)
        {
            throw new BoardNotConnectedException();
        }

        public Task<ChannelMask> TurnOffAsync(ChannelMask relays, CancellationToken cancellationToken = default)
        {
            throw new BoardNotConnectedException();
        }

        public Task<ChannelMask> PulseAsync(ChannelMask relays, TimeSpan length, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            throw new BoardNotConnectedException();
        }
    }
}
