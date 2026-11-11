using System.Collections.Concurrent;
using Contactor.Boards;
using Contactor.Boards.Vm201;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Vm201;

/// <summary>The VM201 simulator as an <see cref="ISimulatedBoard"/>, with the app's client connected to it.</summary>
public sealed class Vm201SimulatedBoardTests : IAsyncDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentQueue<BoardTraceEntry> _traffic = new();
    private readonly CancellationTokenSource _stop = new();
    private Vm201Simulator _board = new();
    private Vm201Client? _client;
    private Task _run = Task.CompletedTask;
    private int _changes;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _run;
        await _board.DisposeAsync();
        _stop.Dispose();
    }

    [Fact]
    public void Should_DescribeItself()
    {
        var board = Assert.IsAssignableFrom<ISimulatedBoard>(_board);
        Assert.IsAssignableFrom<ISimulatedBoardTimers>(_board);

        Assert.Same(Vm201Model.Model, board.Model);
        Assert.False(board.HasLogin);
        Assert.Equal(0, board.Port);
        Assert.Equal(new SimulatedBoardState(ChannelMask.None, ChannelMask.None, []), board.State with { Clients = [] });
    }

    [Fact]
    public async Task Should_SwitchARelayAtTheBoard_AndTellTheApp()
    {
        var client = await ConnectAsync();

        await _board.SwitchRelayAsync(3, turnOn: true);

        await WaitUntilAsync(() => client.State.Outputs == 0x04);
        Assert.Equal(ChannelMask.Of(3), _board.State.Relays);
        await _board.SwitchRelayAsync(3, turnOn: false);
        await WaitUntilAsync(() => client.State.Outputs == 0);
        Assert.True(Volatile.Read(ref _changes) >= 3, "Connecting and both switches are changes.");
    }

    [Fact]
    public async Task Should_SetTheInputAndTheTimers_AndTellTheApp()
    {
        var client = await ConnectAsync();

        await _board.SetInputAsync(1, closed: true);
        await _board.SetTimersAsync(ChannelMask.Of(5));

        await WaitUntilAsync(() => client.State.Input && client.State.TimerMask == 0x10);
        Assert.Equal(ChannelMask.Of(1), _board.State.Inputs);
        Assert.Equal(ChannelMask.Of(5), _board.Timers);
    }

    [Fact]
    public async Task Should_RefuseRelaysAndInputsItDoesNotHave()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _board.SwitchRelayAsync(9, turnOn: true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _board.SwitchRelayAsync(0, turnOn: true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _board.SetInputAsync(2, closed: true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _board.SetTimersAsync(ChannelMask.Of(9)));
    }

    [Fact]
    public async Task Should_ShowWhoIsConnected_UntilTheConnectionDrops()
    {
        var client = await ConnectAsync();
        Assert.StartsWith("127.0.0.1:", Assert.Single(_board.State.Clients), StringComparison.Ordinal);

        await _stop.CancelAsync();
        await _run;

        await WaitUntilAsync(() => _board.State.Clients.Count == 0);
        Assert.False(client.State.IsConnected);
        Assert.Contains(_traffic, e => e.Direction == BoardTraceDirection.None && e.Text.StartsWith("The app at 127.0.0.1:", StringComparison.Ordinal) && e.Text.EndsWith(" disconnected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_TraceBothWays_InPlainWords()
    {
        var client = await ConnectAsync();

        await client.TurnOnAsync(0x04, Cancel);
        await WaitUntilAsync(() => _traffic.Any(e => e.Text == "Status: relay 3 on · input off"));

        var entries = _traffic.ToList();
        Assert.Contains(entries, e => e.Direction == BoardTraceDirection.None && e.Text.StartsWith("The app connected from 127.0.0.1:", StringComparison.Ordinal));
        Assert.Contains(entries, e => e is { Direction: BoardTraceDirection.Received, Text: "Logged in" });
        Assert.Contains(entries, e => e is { Direction: BoardTraceDirection.Sent, Text: "Turn on relay 3", Bytes: "02 06 4F 04 A5 03" });
        Assert.Contains(entries, e => e is { Direction: BoardTraceDirection.Received, Text: "Status: relay 3 on · input off", IsRoutine: false });
        Assert.Contains(entries, e => e is { Direction: BoardTraceDirection.Received, Text: "The name of relay 1", IsRoutine: true });
    }

    [Fact]
    public async Task Should_NeverTraceThePassword()
    {
        await _board.DisposeAsync();
        _board = new Vm201Simulator("john", "secret", NullLogger.Instance);

        await ConnectAsync("john", "secret");

        var entries = _traffic.ToList();
        Assert.Contains(entries, e => e is { Direction: BoardTraceDirection.Sent, Text: "User name (hidden)" });
        Assert.Contains(entries, e => e is { Direction: BoardTraceDirection.Sent, Text: "Password (hidden)", Bytes: "02 0E 57 …" });
        Assert.DoesNotContain(entries, e => e.Text.Contains("secret", StringComparison.Ordinal) || (e.Bytes ?? "").Contains("73 65 63", StringComparison.Ordinal));
        Assert.True(_board.HasLogin);
    }

    [Fact]
    public async Task Should_ReportBrokenBytesFromTheApp_AsAnError()
    {
        _board.Traced += (_, e) => _traffic.Enqueue(e);
        _board.Start();
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync("127.0.0.1", _board.Port, Cancel);

        await tcp.GetStream().WriteAsync(new byte[] { 0x02, 0x06, 0x4F, 0x01, 0x00, 0x03 }, Cancel);

        await WaitUntilAsync(() => _traffic.Any(e => e.IsError));
        var error = _traffic.Single(e => e.IsError);
        Assert.Equal((BoardTraceDirection.Sent, "Skipped 6 broken bytes from the app"), (error.Direction, error.Text));
    }

    [Fact]
    public async Task Should_KeepRunning_WhenASubscriberThrows()
    {
        _board.Traced += (_, _) => throw new InvalidOperationException("A broken page.");
        _board.Changed += (_, _) => throw new InvalidOperationException("A broken page.");

        var client = await ConnectAsync();
        await _board.SwitchRelayAsync(1, turnOn: true);

        await WaitUntilAsync(() => client.State.Outputs == 0x01);
    }

    private async Task<Vm201Client> ConnectAsync(string username = "", string password = "")
    {
        _board.Traced += (_, e) => _traffic.Enqueue(e);
        _board.Changed += (_, _) => Interlocked.Increment(ref _changes);
        _board.Start();
        var options = new Vm201Options { Host = "127.0.0.1", Port = _board.Port, Username = username, Password = password };
        _client = new Vm201Client(Options.Create(options), TimeProvider.System, NullLogger<Vm201Client>.Instance);
        _run = _client.RunAsync(_stop.Token);
        await WaitUntilAsync(() => _client.State.IsConnected);
        return _client;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
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
