using Contactor.Boards;
using Contactor.Boards.Vm204;
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
        Assert.Equal(new BoardCapabilities(4, 4, ReportsChanges: false, AnalogInputCount: 1), Vm204Board.Vm204Capabilities);
        Assert.Equal(("vm204", "Velleman VM204", 80, 31), (Vm204Model.Model.Id, Vm204Model.Model.Name, Vm204Model.Model.DefaultPort, Vm204Model.Model.LoginMaxLength));
    }

    [Fact]
    public void Should_TranslateTheBoardsState()
    {
        var state = Vm204Board.ToBoardState(new Vm204State(true, new Vm204Status(0b1001, 0b0110, 3), At));

        Assert.Equal(new BoardState(true, ChannelMask.Of([1, 4]), ChannelMask.Of([2, 3]), At), state);
        Assert.Equal(BoardState.Initial, Vm204Board.ToBoardState(Vm204State.Initial));
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
        var options = Options.Create(new Vm204Options { Host = "127.0.0.1", Port = simulator.Port, PollSeconds = 1 });
        using var client = new Vm204Client(options, TimeProvider.System, NullLogger<Vm204Client>.Instance);
        var board = new Vm204Board(client, NullLogger<Vm204Board>.Instance);
        BoardState? reported = null;
        board.StateChanged += (_, _) => throw new InvalidOperationException("A broken subscriber.");
        board.StateChanged += (_, state) => Volatile.Write(ref reported, state);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = client.RunAsync(stop.Token);
        await WaitUntilAsync(() => board.State.IsConnected);

        await board.TurnOnAsync(ChannelMask.Of([2, 3]), Cancel);
        await WaitUntilAsync(() => Volatile.Read(ref reported)?.Relays == ChannelMask.Of([2, 3]));
        await simulator.SetInputAsync(3, true);
        await board.RefreshAsync(Cancel);
        await WaitUntilAsync(() => board.State.Inputs == ChannelMask.Of(3));

        await stop.CancelAsync();
        await run;
        Assert.False(board.State.IsConnected);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.RefreshAsync(Cancel));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The condition was not met in time.");
            }

            await Task.Delay(20, Cancel);
        }
    }
}
