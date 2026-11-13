namespace Contactor.Boards;

/// <summary>
/// The pulses a simulated board is running: each ends by itself after its time on the board's
/// <see cref="TimeProvider"/>, as on the board, with no further command from the app. A later pulse of a relay replaces
/// the one it is in, and <see cref="Cancel"/> ends a relay's pulse without its end, as when the relay is switched for
/// good. Thread-safe; once disposed, no pulse ends any more.
/// </summary>
public sealed class SimulatedPulses : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
    private readonly List<Pulse> _pulses = [];
    private bool _disposed;

    public SimulatedPulses(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <summary>The relays (bit 0 = relay 1) whose pulse is still running.</summary>
    public ulong Running
    {
        get
        {
            lock (_lock)
            {
                return _pulses.Aggregate(0UL, (all, p) => all | p.Relays);
            }
        }
    }

    /// <summary>
    /// Ends the pulse of <paramref name="relays"/> after <paramref name="length"/>: then <paramref name="end"/> gets the
    /// relays still in it, all at once. Call it once the relays switched.
    /// </summary>
    public void Start(ulong relays, TimeSpan length, Action<ulong> end)
    {
        ArgumentNullException.ThrowIfNull(end);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(length, TimeSpan.Zero);
        if (relays == 0)
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            RemoveLocked(relays);
            var pulse = new Pulse(relays, end);
            _pulses.Add(pulse);
            pulse.Timer = _timeProvider.CreateTimer(_ => End(pulse), null, length, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Ends the pulse of <paramref name="relays"/> without its end.</summary>
    public void Cancel(ulong relays)
    {
        lock (_lock)
        {
            RemoveLocked(relays);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (var pulse in _pulses)
            {
                pulse.Timer?.Dispose();
            }

            _pulses.Clear();
        }
    }

    private void End(Pulse pulse)
    {
        ulong relays;
        lock (_lock)
        {
            if (_disposed || !_pulses.Remove(pulse))
            {
                return;
            }

            pulse.Timer?.Dispose();
            relays = pulse.Relays;
        }

        pulse.End(relays);
    }

    // Under the lock: takes the relays out of the pulses they are in; a pulse left without relays stops.
    private void RemoveLocked(ulong relays)
    {
        for (var i = _pulses.Count - 1; i >= 0; i--)
        {
            var pulse = _pulses[i];
            pulse.Relays &= ~relays;
            if (pulse.Relays == 0)
            {
                pulse.Timer?.Dispose();
                _pulses.RemoveAt(i);
            }
        }
    }

    private sealed class Pulse
    {
        public Pulse(ulong relays, Action<ulong> end)
        {
            Relays = relays;
            End = end;
        }

        public ulong Relays { get; set; }

        public Action<ulong> End { get; }

        public ITimer? Timer { get; set; }
    }
}
