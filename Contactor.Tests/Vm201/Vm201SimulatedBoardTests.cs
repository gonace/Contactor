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
        Assert.Equal((ChannelMask.None, ChannelMask.None), (board.State.Relays, board.State.Inputs));
        Assert.Empty(board.State.Clients);
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
        Assert.Equal(3, Volatile.Read(ref _changes)); // connecting and both switches
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

        await WaitUntilAsync(() => _traffic.Any(e => e.Direction == BoardTraceDirection.None && e.Text.StartsWith("The app at 127.0.0.1:", StringComparison.Ordinal) && e.Text.EndsWith(" disconnected", StringComparison.Ordinal)));
        Assert.Empty(_board.State.Clients);
        Assert.False(client.State.IsConnected);
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
    public async Task Should_GiveEachConnectionItsFirstStatus_AsNotRoutine()
    {
        var first = await ConnectAsync();
        await _stop.CancelAsync();
        await _run;
        await WaitUntilAsync(() => _board.State.Clients.Count == 0);
        _traffic.Clear();

        using var stop = new CancellationTokenSource();
        var second = CreateClient(_board.Port);
        var run = second.RunAsync(stop.Token);
        await WaitUntilAsync(() => second.State.IsConnected);
        await stop.CancelAsync();
        await run;

        // The client is connected once it has the status; the board traces it just after sending it.
        await WaitUntilAsync(() => _traffic.Any(e => e.Direction == BoardTraceDirection.Received && e.Text.StartsWith("Status:", StringComparison.Ordinal)));
        var status = _traffic.First(e => e.Direction == BoardTraceDirection.Received && e.Text.StartsWith("Status:", StringComparison.Ordinal));
        Assert.False(status.IsRoutine);
        Assert.False(first.State.IsConnected);
    }

    [Fact]
    public async Task Should_TellEveryClient_AndListThemAll()
    {
        var first = await ConnectAsync();
        using var stop = new CancellationTokenSource();
        var second = CreateClient(_board.Port);
        var run = second.RunAsync(stop.Token);
        await WaitUntilAsync(() => second.State.IsConnected && _board.State.Clients.Count == 2);
        _traffic.Clear();

        await _board.SwitchRelayAsync(2, turnOn: true);

        await WaitUntilAsync(() => first.State.Outputs == 0x02 && second.State.Outputs == 0x02);
        var pushed = _traffic.Where(e => e.Text == "Status: relay 2 on · input off").ToList();
        Assert.Equal(2, pushed.Count);
        Assert.All(pushed, e => Assert.False(e.IsRoutine));
        Assert.Equal(_board.State.Clients.Order(StringComparer.Ordinal), _board.State.Clients);
        await stop.CancelAsync();
        await run;
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
    public async Task Should_KeepOnlyTheNewestThousandCommands()
    {
        _board.Start();
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync("127.0.0.1", _board.Port, Cancel);
        List<byte> packets = [];
        for (var i = 0; i < 1000; i++)
        {
            packets.AddRange(Vm201PacketCodec.Encode(Vm201Command.StatusRequest));
        }

        for (byte mask = 1; mask <= 5; mask++)
        {
            packets.AddRange(Vm201PacketCodec.Encode(Vm201Command.On, mask));
        }

        await tcp.GetStream().WriteAsync(packets.ToArray(), Cancel);

        await WaitUntilAsync(() => _board.ReceivedCommands.Count > 0 && _board.ReceivedCommands[^1].Command == Vm201Command.On && _board.ReceivedCommands[^1].Data.Span[0] == 5);
        var received = _board.ReceivedCommands;
        Assert.Equal(1000, received.Count);
        Assert.Equal(995, received.Count(p => p.Command == Vm201Command.StatusRequest));
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
        _client = CreateClient(_board.Port, username, password);
        _run = _client.RunAsync(_stop.Token);
        await WaitUntilAsync(() => _client.State.IsConnected);
        return _client;
    }

    private static Vm201Client CreateClient(int port, string username = "", string password = "")
    {
        var options = new Vm201Options { Host = "127.0.0.1", Port = port, Username = username, Password = password };
        return new Vm201Client(Options.Create(options), TimeProvider.System, NullLogger<Vm201Client>.Instance);
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
