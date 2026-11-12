using Contactor.Boards;
using Contactor.Boards.Vm208;
using Contactor.Tests.Vm208;
using Contactor.Tests.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Boards;

/// <summary>The VM208 as an <see cref="IRelayBoard"/>: its state and commands translated, its changes passed on.</summary>
public sealed class Vm208BoardTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_HaveFourRelaysAndOneInput_AndNotReportChanges()
    {
        // The two transistor outputs, m1 and m2, aren't relays.
        Assert.Equal(new BoardCapabilities(4, 1, ReportsChanges: false), Vm208Board.Vm208Capabilities);
        Assert.Equal(("vm208", "Velleman VM208", 80, 0, null), (Vm208Model.Model.Id, Vm208Model.Model.Name, Vm208Model.Model.DefaultPort, Vm208Model.Model.LoginMaxLength, Vm208Model.Model.LoginHint));
    }

    [Fact]
    public void Should_TranslateTheBoardsState()
    {
        var state = Vm208Board.ToBoardState(new Vm208State(true, new Vm208Status(0b1001, true, 0b11), At));

        Assert.Equal(new BoardState(true, ChannelMask.Of([1, 4]), ChannelMask.Of(1), At), state);
        Assert.Equal(ChannelMask.None, Vm208Board.ToBoardState(new Vm208State(true, new Vm208Status(0, false, 0), At)).Inputs);
        Assert.Equal(BoardState.Initial, Vm208Board.ToBoardState(Vm208State.Initial));
    }

    [Fact]
    public void Should_SendRelaysOneToFour_AndRefuseOthers()
    {
        Assert.Equal(0b1001, Vm208Board.ToMask(ChannelMask.Of([1, 4])));
        Assert.Throws<ArgumentOutOfRangeException>(() => Vm208Board.ToMask(ChannelMask.Of(5)));
    }

    [Fact]
    public async Task Should_SwitchTheBoard_AndPassOnItsState()
    {
        var time = new ManualTimeProvider();
        await using var simulator = new Vm208Simulator();
        simulator.Start();
        var options = Options.Create(new Vm208Options { Host = "127.0.0.1", Port = simulator.Port });
        using var client = new Vm208Client(options, time, NullLogger<Vm208Client>.Instance);
        var board = new Vm208Board(client, NullLogger<Vm208Board>.Instance);
        BoardState? reported = null;
        board.StateChanged += (_, _) => throw new InvalidOperationException("A broken subscriber.");
        board.StateChanged += (_, state) => Volatile.Write(ref reported, state);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = client.RunAsync(stop.Token);
        await Wait.UntilAsync(() => board.State.IsConnected);

        await board.TurnOnAsync(ChannelMask.Of([2, 3]), Cancel);
        Assert.Equal(ChannelMask.Of([2, 3]), Volatile.Read(ref reported)?.Relays);
        await simulator.SetInputAsync(1, true);
        await Wait.UntilAsync(() => time.PendingTimers == 1);
        await board.RefreshAsync(Cancel);
        await Wait.UntilAsync(() => board.State.Inputs == ChannelMask.Of(1));

        await stop.CancelAsync();
        await run;
        Assert.False(board.State.IsConnected);
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.RefreshAsync(Cancel));
    }
}
