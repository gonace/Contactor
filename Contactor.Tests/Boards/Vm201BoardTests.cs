using Contactor.Boards;
using Contactor.Boards.Vm201;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Boards;

/// <summary>The VM201 as an <see cref="IRelayBoard"/>: its state and commands translated, and its status passed on.</summary>
public sealed class Vm201BoardTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_HaveEightRelaysAndOneInput()
    {
        Assert.Equal(new BoardCapabilities(8, 1, ReportsChanges: true), Vm201Board.Vm201Capabilities);
        Assert.Equal(ChannelMask.FirstN(8), Vm201Board.Vm201Capabilities.AllRelays);
    }

    [Fact]
    public void Should_TranslateTheBoardsStatus()
    {
        var state = Vm201Board.ToBoardState(new Vm201State(true, 0b1000_0101, 0b0000_0010, true, At));

        Assert.Equal(new BoardState(true, ChannelMask.Of([1, 3, 8]), ChannelMask.Of(1), At), state);
        Assert.True(state.IsOn(8));
        Assert.False(state.IsOn(2));
        Assert.Equal(ChannelMask.None, Vm201Board.ToBoardState(Vm201State.Initial).Inputs);
    }

    [Fact]
    public void Should_SendRelaysOneToEight_AndRefuseOthers()
    {
        Assert.Equal(0b1000_0001, Vm201Board.ToMask(ChannelMask.Of([1, 8])));
        Assert.Equal(0, Vm201Board.ToMask(ChannelMask.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => Vm201Board.ToMask(ChannelMask.Of(9)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Vm201Board.ToMask(ChannelMask.Of([1, 64])));
    }

    [Fact]
    public async Task Should_SwitchTheBoard_AndPassOnItsStatus()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        var client = CreateClient(simulator.Port);
        var board = new Vm201Board(client, NullLogger<Vm201Board>.Instance);
        BoardState? reported = null;
        board.StateChanged += (_, state) => reported = state;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = client.RunAsync(stop.Token);
        await WaitUntilAsync(() => board.State.IsConnected);

        await board.TurnOnAsync(ChannelMask.Of([2, 4]), Cancel);
        await WaitUntilAsync(() => reported?.Relays == ChannelMask.Of([2, 4]));
        await board.TurnOffAsync(ChannelMask.Of(2), Cancel);
        await WaitUntilAsync(() => board.State.Relays == ChannelMask.Of(4));

        Assert.Equal(0b0000_1000, simulator.Outputs);
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
    }

    [Fact]
    public async Task Should_GiveEverySubscriberTheState_WhenOneFails()
    {
        await using var simulator = new Vm201Simulator();
        simulator.Start();
        var client = CreateClient(simulator.Port);
        var board = new Vm201Board(client, NullLogger<Vm201Board>.Instance);
        board.StateChanged += (_, _) => throw new InvalidOperationException("A broken subscriber.");
        BoardState? reported = null;
        board.StateChanged += (_, state) => reported = state;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = client.RunAsync(stop.Token);

        await WaitUntilAsync(() => reported?.IsConnected == true);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void Should_RefuseARelayTheBoardDoesntHave(int relay)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Vm201Board.Vm201Capabilities.Relay(relay));
    }

    [Fact]
    public async Task Should_RefuseCommands_WithoutTheBoard()
    {
        var board = new Vm201Board(CreateClient(port: 1), NullLogger<Vm201Board>.Instance);

        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.TurnOnAsync(ChannelMask.Of(1), Cancel));
        await Assert.ThrowsAsync<BoardNotConnectedException>(() => board.RefreshAsync(Cancel));
    }

    private static Vm201Client CreateClient(int port)
    {
        var options = new Vm201Options { Host = "127.0.0.1", Port = port };
        return new Vm201Client(Options.Create(options), TimeProvider.System, NullLogger<Vm201Client>.Instance);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
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
