using System.Globalization;
using Contactor.Boards;
using Contactor.Boards.Shelly;

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
    private readonly CancellationTokenSource _stopping = new();
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
        Shelly = board is ShellySimulator shelly ? new ShellyControls(shelly, text => Add(TrafficKind.AtBoard, text)) : null;
        Switches = new BoardSwitches(board, text => Add(TrafficKind.AtBoard, text));
        Board.Traced += OnTraced;
        Board.Changed += OnChanged;
    }

    /// <summary>Raised when the board's state or its traffic changes, from any thread: handlers must be quick.</summary>
    public event EventHandler? Changed;

    /// <summary>Its number in the simulator, for the page.</summary>
    public int Id { get; }

    public ISimulatedBoard Board { get; }

    /// <summary>Its short name on the page, its model's id in capitals, e.g. "VM201" or "SHELLY-2".</summary>
    public string ShortName => Board.Model.Id.ToUpperInvariant();

    /// <summary>The user name it asks for, or null without a login. The password is never shown.</summary>
    public string? Username { get; }

    /// <summary>The controls for a Shelly's special cases, or null for any other board.</summary>
    public ShellyControls? Shelly { get; }

    /// <summary>The switches for what the board can do wrong: unreachable, hung, a relay refused, and its model's own.</summary>
    public BoardSwitches Switches { get; }

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
        // Without power nothing switches, not even the board's own buttons.
        if (Power?.IsPoweredOff == true)
        {
            return Task.CompletedTask;
        }

        Add(TrafficKind.AtBoard, string.Create(CultureInfo.InvariantCulture, $"Relay {relay} switched {(turnOn ? "on" : "off")} here"));
        return Board.SwitchRelayAsync(relay, turnOn);
    }

    public Task SetInputAsync(int input, bool closed)
    {
        Add(TrafficKind.AtBoard, string.Create(CultureInfo.InvariantCulture, $"Input {input} {(closed ? "closed" : "opened")} here"));
        return Board.SetInputAsync(input, closed);
    }

    /// <summary>
    /// Sets what an analog input reads, in counts of the board's converter, as a sensor at its terminal would. Nothing
    /// for a count it already reads, or for an input or count the board doesn't have: the page offers only those.
    /// </summary>
    public Task SetAnalogAsync(int input, int counts)
    {
        var analog = Board.State.Analog;
        if (Board.Model.Capabilities.Analog is not { } ability
            || input < 1
            || input > analog.Count
            || counts < 0
            || counts > ability.MaxCounts
            || analog[input - 1] == counts)
        {
            return Task.CompletedTask;
        }

        Add(TrafficKind.AtBoard, string.Create(
            CultureInfo.InvariantCulture,
            $"Analog input {input} set to {counts} ({ability.Volts(counts):0.00} V) here"));
        return Board.SetAnalogAsync(input, counts);
    }

    /// <summary>
    /// Turns one relay's own timer on or off, as the board's own web page would; the others stay as they are. Nothing for
    /// a board without timers, or for a relay it doesn't have: the page offers only those.
    /// </summary>
    public Task SetTimerAsync(int relay, bool turnOn)
    {
        if (Board is not ISimulatedBoardTimers timers || relay < 1 || relay > Board.Model.Capabilities.RelayCount)
        {
            return Task.CompletedTask;
        }

        Add(TrafficKind.AtBoard, string.Create(
            CultureInfo.InvariantCulture,
            $"The board's own timer for relay {relay} turned {(turnOn ? "on" : "off")} here"));
        return timers.SetTimerAsync(relay, turnOn);
    }

    /// <summary>
    /// Sets one relay's control mode, as the board's own tool or web page would; the others stay as they are. Nothing
    /// for a board without modes, or for a relay or mode it doesn't have: the page offers only those.
    /// </summary>
    public Task SetModeAsync(int relay, int mode)
    {
        if (Board is not ISimulatedBoardRelayModes modes || relay < 1 || relay > modes.Modes.Count || mode < 0 || mode >= modes.ModeNames.Count)
        {
            return Task.CompletedTask;
        }

        Add(TrafficKind.AtBoard, string.Create(
            CultureInfo.InvariantCulture,
            $"Relay {relay} set to {modes.ModeNames[mode]} mode here"));
        return modes.SetModeAsync(relay, mode);
    }

    /// <summary>The board's power, which the page can cut; null for a board without (none today).</summary>
    public ISimulatedBoardPower? Power => Board as ISimulatedBoardPower;

    /// <summary>
    /// Cuts the board's power for <paramref name="off"/> on the simulator's clock, as a power cut would: the app loses it,
    /// then it comes back with its relays as after a power cut. Ends once the power is back. Nothing while it is cut
    /// already, for a board without power, or for a length that isn't positive.
    /// </summary>
    public Task PowerCutAsync(TimeSpan off)
    {
        return CutAsync(off, string.Create(CultureInfo.InvariantCulture, $"Power cut for {Length(off)} here"));
    }

    /// <summary>Restarts the board: a power cut of <see cref="RestartLength"/>.</summary>
    public Task RestartAsync()
    {
        return CutAsync(RestartLength, "Restarted here");
    }

    /// <summary>How long a restart is without power.</summary>
    public static TimeSpan RestartLength { get; } = TimeSpan.FromSeconds(1);

    private async Task CutAsync(TimeSpan off, string text)
    {
        if (Power is not { } power || power.IsPoweredOff || off <= TimeSpan.Zero || _stopping.IsCancellationRequested)
        {
            return;
        }

        Add(TrafficKind.AtBoard, text);
        await power.CutPowerAsync().ConfigureAwait(false);
        RaiseChanged();
        try
        {
            await Task.Delay(off, _time, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The board was removed or the simulator stops: it stays without power.
            return;
        }

        await power.RestorePowerAsync().ConfigureAwait(false);
        Add(TrafficKind.AtBoard, $"Power back here. {power.AfterPowerCut}");
    }

    // "5 s", "30 s" or "2 min".
    private static string Length(TimeSpan length)
    {
        return length.TotalSeconds % 60 == 0 && length >= TimeSpan.FromMinutes(1)
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)length.TotalMinutes} min")
            : string.Create(CultureInfo.InvariantCulture, $"{length.TotalSeconds:0.###} s");
    }

    public void DropConnections()
    {
        Add(TrafficKind.AtBoard, "Connections dropped here");
        Board.DropConnections();
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await Board.DisposeAsync().ConfigureAwait(false);
        Board.Traced -= OnTraced;
        Board.Changed -= OnChanged;
        _stopping.Dispose();
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
