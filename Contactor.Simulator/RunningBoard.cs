using System.Globalization;
using Contactor.Boards;

namespace Contactor.Simulator;

/// <summary>What a line in a board's traffic is.</summary>
public enum TrafficKind
{
    /// <summary>A message from the app to the board.</summary>
    AppToBoard,

    /// <summary>A message from the board to the app.</summary>
    BoardToApp,

    /// <summary>Something done at the board itself, from the page: a relay switched, an input set.</summary>
    AtBoard,

    /// <summary>The app connecting or going away.</summary>
    Connection,
}

/// <summary>One line in a board's traffic on the page.</summary>
/// <param name="Id">Increases by one per line, so the page can tell new ones apart.</param>
/// <param name="Bytes">The message's bytes as text, or null when there is no message.</param>
public sealed record TrafficItem(long Id, DateTimeOffset At, TrafficKind Kind, string Text, string? Bytes, bool IsError);

/// <summary>
/// A simulated board running in the simulator, with its traffic: the latest <see cref="Capacity"/> lines, in memory
/// only. The keep-alive is left out and only counted.
/// </summary>
public sealed class RunningBoard : IAsyncDisposable
{
    public const int Capacity = 100;

    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Queue<TrafficItem> _items = new(Capacity);
    private long _nextId = 1;
    private int _hiddenRoutine;

    public RunningBoard(int id, ISimulatedBoard board, string? username, bool fromSettings, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(board);
        Id = id;
        Board = board;
        Username = username;
        FromSettings = fromSettings;
        _time = time;
        Board.Traced += OnTraced;
        Board.Changed += OnChanged;
    }

    /// <summary>Raised when the board's state or its traffic changes, from any thread: handlers must be quick.</summary>
    public event EventHandler? Changed;

    /// <summary>Its number in the simulator, for the page.</summary>
    public int Id { get; }

    public ISimulatedBoard Board { get; }

    /// <summary>The user name it asks for, or null without a login. The password is never shown.</summary>
    public string? Username { get; }

    /// <summary>Started from the settings, so it starts every time; otherwise added on the page.</summary>
    public bool FromSettings { get; }

    /// <summary>The lines, newest first.</summary>
    public IReadOnlyList<TrafficItem> Traffic
    {
        get
        {
            lock (_lock)
            {
                return [.. _items.Reverse()];
            }
        }
    }

    /// <summary>How many keep-alive messages were left out of <see cref="Traffic"/>.</summary>
    public int HiddenRoutine => Volatile.Read(ref _hiddenRoutine);

    public void ClearTraffic()
    {
        lock (_lock)
        {
            _items.Clear();
            _hiddenRoutine = 0;
        }

        RaiseChanged();
    }

    /// <summary>Switches a relay as the board's own button would.</summary>
    public Task SwitchRelayAsync(int relay, bool turnOn)
    {
        Add(TrafficKind.AtBoard, string.Create(CultureInfo.InvariantCulture, $"Relay {relay} switched {(turnOn ? "on" : "off")} here"));
        return Board.SwitchRelayAsync(relay, turnOn);
    }

    public Task SetInputAsync(int input, bool closed)
    {
        Add(TrafficKind.AtBoard, string.Create(CultureInfo.InvariantCulture, $"Input {input} {(closed ? "closed" : "opened")} here"));
        return Board.SetInputAsync(input, closed);
    }

    /// <summary>Turns the board's own timers on for every relay, or off. Nothing for a board without timers.</summary>
    public Task SetTimersAsync(bool on)
    {
        if (Board is not ISimulatedBoardTimers timers)
        {
            return Task.CompletedTask;
        }

        Add(TrafficKind.AtBoard, on ? "The board's own timers turned on here" : "The board's own timers turned off here");
        return timers.SetTimersAsync(on ? Board.Model.Capabilities.AllRelays : ChannelMask.None);
    }

    public void DropConnections()
    {
        Add(TrafficKind.AtBoard, "Connections dropped here");
        Board.DropConnections();
    }

    public async ValueTask DisposeAsync()
    {
        await Board.DisposeAsync().ConfigureAwait(false);
        Board.Traced -= OnTraced;
        Board.Changed -= OnChanged;
    }

    private void OnTraced(object? sender, BoardTraceEntry entry)
    {
        if (entry.IsRoutine)
        {
            Interlocked.Increment(ref _hiddenRoutine);
            RaiseChanged();
            return;
        }

        var kind = entry.Direction switch
        {
            BoardTraceDirection.Sent => TrafficKind.AppToBoard,
            BoardTraceDirection.Received => TrafficKind.BoardToApp,
            _ => TrafficKind.Connection,
        };
        Add(kind, entry.Text, entry.Bytes, entry.IsError, entry.At);
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        RaiseChanged();
    }

    private void Add(TrafficKind kind, string text, string? bytes = null, bool isError = false, DateTimeOffset? at = null)
    {
        lock (_lock)
        {
            if (_items.Count == Capacity)
            {
                _items.Dequeue();
            }

            _items.Enqueue(new TrafficItem(_nextId++, at ?? _time.GetUtcNow(), kind, text, bytes, isError));
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
