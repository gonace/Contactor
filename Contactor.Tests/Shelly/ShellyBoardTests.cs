using Contactor.Boards;
using Contactor.Boards.Shelly;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Shelly;

/// <summary>The catalogue entries, the login check and the adapters for the app.</summary>
public sealed class ShellyBoardTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_ListOneEntryPerRelayCount()
    {
        Assert.Equal(["shelly-1", "shelly-2", "shelly-3", "shelly-4"], ShellyModels.All.Select(m => m.Id));
        Assert.All(ShellyModels.All, m =>
        {
            Assert.Equal("Shelly", m.Brand);
            Assert.Equal(0, m.Capabilities.InputCount);
            Assert.False(m.Capabilities.ReportsChanges);
            Assert.Equal(OwnRulesAbility.CantTell, m.Capabilities.OwnRules);
            Assert.Equal(new PowerOnAbility([PowerOnState.Off, PowerOnState.AsBefore, PowerOnState.On, PowerOnState.FollowsInput], perRelay: true), m.Capabilities.PowerOn);
            Assert.Null(m.Capabilities.Pulse);
            Assert.True(m.PasswordOnly);
            Assert.False(m.LoginRequired);
            Assert.Equal(80, m.DefaultPort);
            Assert.Equal(ShellyApi.PasswordMaxLength, m.LoginMaxLength);
        });
        Assert.Equal([1, 2, 3, 4], ShellyModels.All.Select(m => m.Capabilities.RelayCount));
        Assert.Equal("Shelly Gen2+ 1", ShellyModels.OneRelay.Name);
        Assert.Equal("Shelly Gen2+ 4", ShellyModels.FourRelays.Name);
        Assert.Same(ShellyModels.ThreeRelays, ShellyModels.ModelFor(3));
        Assert.Null(ShellyModels.ModelFor(0));
        Assert.Null(ShellyModels.ModelFor(5));
    }

    [Fact]
    public void Should_TakeAPasswordOnly()
    {
        using var client = new ShellyClient(Options.Create(new ShellyOptions { Host = "127.0.0.1" }), ShellyModels.OneRelay, new ManualTimeProvider(), NullLogger<ShellyClient>.Instance);
        var connection = new ShellyConnection(client, Options.Create(new ShellyOptions { Host = "10.0.0.5", Password = "x" }));

        Assert.Equal(new LoginProblem(LoginProblemKind.PasswordOnly, LoginField.UserName, Board: "Shelly"), connection.CheckLogin(new BoardLogin("admin", "x")));
        Assert.Equal(new LoginProblem(LoginProblemKind.PasswordTooLong, MaxLength: ShellyApi.PasswordMaxLength), connection.CheckLogin(new BoardLogin("", new string('x', ShellyApi.PasswordMaxLength + 1))));
        Assert.Null(connection.CheckLogin(new BoardLogin("", "lösen ord \"med\" tecken")));
        Assert.Null(connection.CheckLogin(BoardLogin.None));
        Assert.Throws<ArgumentException>(() => connection.SetLogin(new BoardLogin("admin", "x")));

        connection.SetLogin(new BoardLogin("", "new"));
        Assert.Equal(new BoardLogin("", "new"), connection.Login);
        Assert.Equal(new BoardLogin("", "x"), connection.DefaultLogin);
        Assert.Equal(new BoardEndpoint("10.0.0.5", 80), connection.DefaultEndpoint);
    }

    [Fact]
    public async Task Should_SwitchAndReport_ThroughTheBoard()
    {
        await using var simulator = new ShellySimulator(ShellyModels.TwoRelays);
        simulator.Start();
        var time = new ManualTimeProvider();
        using var client = new ShellyClient(Options.Create(new ShellyOptions { Host = "127.0.0.1", Port = simulator.Port, PollSeconds = 60 }), ShellyModels.TwoRelays, time, NullLogger<ShellyClient>.Instance);
        var board = new ShellyBoard(client, NullLogger<ShellyBoard>.Instance);
        using var traffic = new ShellyTraffic(client);
        var traced = 0;
        traffic.Traced += (_, _) => Interlocked.Increment(ref traced);
        List<BoardState> states = [];
        board.StateChanged += (_, _) => throw new InvalidOperationException("A subscriber's bug.");
        board.StateChanged += (_, state) =>
        {
            lock (states)
            {
                states.Add(state);
            }
        };
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(cts.Token);
        try
        {
            await Wait.UntilAsync(() => board.State.IsConnected);

            Assert.Equal(ChannelMask.Of(2), await board.TurnOnAsync(ChannelMask.Of(2), Cancel));
            Assert.Equal(ChannelMask.Of(2), board.State.Relays);
            Assert.Equal(ChannelMask.None, board.State.Inputs);
            Assert.Null(board.State.Analog);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.TurnOnAsync(ChannelMask.Of(3), Cancel));
            await Assert.ThrowsAsync<NotSupportedException>(() => board.PulseAsync(ChannelMask.Of(1), TimeSpan.FromSeconds(1), Cancel));
            await board.RefreshAsync(Cancel);
            Assert.Equal(2, board.Capabilities.RelayCount);
            lock (states)
            {
                Assert.Contains(states, s => s.Relays == ChannelMask.Of(2));
            }

            Assert.True(Volatile.Read(ref traced) > 0);
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task Should_ReportAndSetPowerOnStates_ThroughTheBoard()
    {
        await using var simulator = new ShellySimulator(ShellyModels.TwoRelays);
        simulator.SetInitialState(2, "restore_last");
        simulator.Start();
        var time = new ManualTimeProvider();
        using var client = new ShellyClient(Options.Create(new ShellyOptions { Host = "127.0.0.1", Port = simulator.Port, PollSeconds = 60 }), ShellyModels.TwoRelays, time, NullLogger<ShellyClient>.Instance);
        var board = new ShellyBoard(client, NullLogger<ShellyBoard>.Instance);
        List<BoardState> states = [];
        List<BoardState> powerOns = [];
        board.StateChanged += (_, state) =>
        {
            lock (states)
            {
                states.Add(state);
            }
        };
        board.PowerOnChanged += (_, _) => throw new InvalidOperationException("A subscriber's bug.");
        board.PowerOnChanged += (_, state) =>
        {
            lock (powerOns)
            {
                powerOns.Add(state);
            }
        };
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(cts.Token);
        try
        {
            await Wait.UntilAsync(() => board.State.IsConnected);
            Assert.Equal(new PowerOnStates([PowerOnState.Off, PowerOnState.AsBefore]), board.State.PowerOn);
            int statesBefore;
            lock (states)
            {
                statesBefore = states.Count;
            }

            await board.SetPowerOnAsync(1, PowerOnState.On, Cancel);

            Assert.Equal("on", simulator.InitialState(1));
            Assert.Equal(new PowerOnStates([PowerOnState.On, PowerOnState.AsBefore]), board.State.PowerOn);
            lock (powerOns)
            {
                Assert.Equal(new PowerOnStates([PowerOnState.On, PowerOnState.AsBefore]), Assert.Single(powerOns).PowerOn);
            }

            // Only the power-on states changed: no state for the safety services.
            lock (states)
            {
                Assert.Equal(statesBefore, states.Count);
            }

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => board.SetPowerOnAsync(3, PowerOnState.Off, Cancel));
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }
}
