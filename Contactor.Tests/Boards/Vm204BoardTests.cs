using Contactor.Boards;
using Contactor.Boards.Velleman;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Boards;

/// <summary>The VM204 as an <see cref="IRelayBoard"/>: its state and commands translated, its changes passed on.</summary>
public sealed class Vm204BoardTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_HaveFourRelaysFourInputsAndAnAnalogInput_AndNotReportChanges()
    {
        Assert.Equal(new BoardCapabilities(4, 4, ReportsChanges: false, Analog: new AnalogAbility(1, 1023, 6.6m)), Vm204Board.Vm204Capabilities);
        Assert.Equal(1, Vm204Board.Vm204Capabilities.AnalogInputCount);
        Assert.Equal(("vm204", "Velleman VM204", 80, 31), (Vm204Model.Model.Id, Vm204Model.Model.Name, Vm204Model.Model.DefaultPort, Vm204Model.Model.LoginMaxLength));
    }

    [Fact]
    public void Should_TranslateTheBoardsState()
    {
        var state = Vm204Board.ToBoardState(new Vm204State(true, new Vm204Status(0b1001, 0b0110, 3), At));

        Assert.Equal(new BoardState(true, ChannelMask.Of([1, 4]), ChannelMask.Of([2, 3]), At, Analog: new AnalogReadings([3])), state);
        Assert.Equal(BoardState.Initial, Vm204Board.ToBoardState(Vm204State.Initial));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(512, 512)]
    [InlineData(1023, 1023)]
    [InlineData(null, null)]
    [InlineData(-1, null)]
    [InlineData(1024, null)]
    [InlineData(int.MaxValue, null)]
    public void Should_ReadTheAnalogInput_AndHaveNoReadingForAValueTheConverterCantGive(int? analog, int? expected)
    {
        var state = Vm204Board.ToBoardState(new Vm204State(true, new Vm204Status(0, 0, analog), At));

        Assert.Equal(new AnalogReadings([expected]), state.Analog);
        Assert.Equal(expected, state.Analog!.Input(1));
    }

    [Fact]
    public void Should_Read1023As6Point6Volts_AsTheFirmwareDoes()
    {
        var analog = Vm204Board.Vm204Analog;

        Assert.Equal(6.6m, analog.Volts(1023));
        Assert.Equal(0m, analog.Volts(0));
        Assert.Equal(3.30, Math.Round((double)analog.Volts(512), 2));
        Assert.Equal(6.6m / 1023, analog.VoltsPerCount);
        Assert.True(analog.IsFullScale(1023));
        Assert.False(analog.IsFullScale(1022));
    }

    [Fact]
    public async Task Should_PassOnAnAnalogChangeAlone_AsAnAnalogChange_NeverAsAStateChange()
    {
        await using var simulator = new Vm204Simulator();
        simulator.Start();
        await simulator.SetAnalogAsync(1, 100);
        var options = Options.Create(new Vm204Options { Host = "127.0.0.1", Port = simulator.Port, PollSeconds = 2 });
        using var client = new Vm204Client(options, new ManualTimeProvider(), NullLogger<Vm204Client>.Instance);
        var board = new Vm204Board(client, NullLogger<Vm204Board>.Instance);
        var states = new List<BoardState>();
        var analogs = new List<BoardState>();
        board.StateChanged += (_, state) =>
        {
            lock (states)
            {
                states.Add(state);
            }
        };
        board.AnalogChanged += (_, _) => throw new InvalidOperationException("A broken subscriber.");
        board.AnalogChanged += (_, state) =>
        {
            lock (analogs)
            {
                analogs.Add(state);
            }
        };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = client.RunAsync(stop.Token);
        await Wait.UntilAsync(() => Count(states) == 1);
        Assert.Equal(new AnalogReadings([100]), board.State.Analog);

        // The analog value alone: an analog change, and nothing for the safety services.
        await simulator.SetAnalogAsync(1, 700);
        await board.RefreshAsync(Cancel);
        await Wait.UntilAsync(() => Count(analogs) == 1);
        Assert.Equal(1, Count(states));
        Assert.Equal(new AnalogReadings([700]), analogs[0].Analog);
        Assert.Equal(new AnalogReadings([700]), board.State.Analog);

        // With a relay: a state change, which carries the new value too, and no analog change of its own.
        await simulator.SetAnalogAsync(1, 800);
        await simulator.SwitchRelayAsync(1, true);
        await board.RefreshAsync(Cancel);
        await Wait.UntilAsync(() => Count(states) == 2);
        Assert.Equal(new AnalogReadings([800]), states[1].Analog);

        await stop.CancelAsync();
        await run;
        Assert.Equal(1, Count(analogs));
        Assert.Equal(new AnalogReadings([800]), board.State.Analog);
    }

    // 1500 and 1600 are both outside what the converter gives: no reading either way, so no analog change.
    [Fact]
    public async Task Should_PassOnNoAnalogChange_BetweenTwoValuesTheConverterCantGive()
    {
        await using var scripted = Velleman.Vm204ClientTests.ScriptedBoard.Start();
        scripted.Analog = 1500;
        var options = Options.Create(new Vm204Options { Host = "127.0.0.1", Port = scripted.Port, PollSeconds = 2 });
        using var client = new Vm204Client(options, new ManualTimeProvider(), NullLogger<Vm204Client>.Instance);
        var board = new Vm204Board(client, NullLogger<Vm204Board>.Instance);
        var analogs = new List<BoardState>();
        board.AnalogChanged += (_, state) =>
        {
            lock (analogs)
            {
                analogs.Add(state);
            }
        };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = client.RunAsync(stop.Token);
        await Wait.UntilAsync(() => client.State.Status?.Analog == 1500);
        Assert.Equal(new AnalogReadings([null]), board.State.Analog);

        scripted.Analog = 1600;
        await board.RefreshAsync(Cancel);
        await Wait.UntilAsync(() => client.State.Status?.Analog == 1600);
        scripted.Analog = 700;
        await board.RefreshAsync(Cancel);
        await Wait.UntilAsync(() => Count(analogs) == 1);

        await stop.CancelAsync();
        await run;
        Assert.Equal(new AnalogReadings([700]), Assert.Single(analogs).Analog);
    }

    private static int Count(List<BoardState> list)
    {
        lock (list)
        {
            return list.Count;
        }
    }

    [Fact]
    public void Should_PassTheProblemOn()
    {
        var problem = new ConnectionProblem(ConnectionProblemKind.LoginNeeded, Vm204Model.Model);

        var state = Vm204Board.ToBoardState(Vm204State.Initial with { Problem = problem });

        Assert.Equal(BoardState.Initial with { Problem = problem }, state);
    }

    [Fact]
    public void Should_SendRelaysOneToFour_AndRefuseOthers()
    {
        Assert.Equal(0b1001, Vm204Board.ToMask(ChannelMask.Of([1, 4])));
        Assert.Throws<ArgumentOutOfRangeException>(() => Vm204Board.ToMask(ChannelMask.Of(5)));
    }

    [Fact]
    public async Task Should_SwitchTheBoard_AndPassOnItsState()
    {
        await using var simulator = new Vm204Simulator();
        simulator.Start();

        // No poll comes on its own: the clock only moves when the test moves it, so the command's answer and the read
        // RefreshAsync asks for are what the state shows.
        var options = Options.Create(new Vm204Options { Host = "127.0.0.1", Port = simulator.Port, PollSeconds = 2 });
        using var client = new Vm204Client(options, new ManualTimeProvider(), NullLogger<Vm204Client>.Instance);
        var board = new Vm204Board(client, NullLogger<Vm204Board>.Instance);
        BoardState? reported = null;
        board.StateChanged += (_, _) => throw new InvalidOperationException("A broken subscriber.");
        board.StateChanged += (_, state) => Volatile.Write(ref reported, state);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = client.RunAsync(stop.Token);
        await Wait.UntilAsync(() => board.State.IsConnected);

        Assert.Equal(ChannelMask.Of([2, 3]), await board.TurnOnAsync(ChannelMask.Of([2, 3]), Cancel));
        await Wait.UntilAsync(() => Volatile.Read(ref reported)?.Relays == ChannelMask.Of([2, 3]));
        await simulator.SetInputAsync(3, true);
        await board.RefreshAsync(Cancel);
        await Wait.UntilAsync(() => board.State.Inputs == ChannelMask.Of(3));

        await stop.CancelAsync();
        await run;
        Assert.False(board.State.IsConnected);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.RefreshAsync(Cancel));
    }
}
