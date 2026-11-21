using Contactor.Boards;

namespace Contactor.Web.Features.Relays;

/// <summary>
/// The pulses the app started on one board (<c>LiveBoard.Pulses</c>), by channel: the one place that says whether a
/// relay's ON and OFF are locked. While a pulse the app started runs, nobody switches the relay by hand
/// (<see cref="RelayControl"/> refuses, and the pages show "Pulsing…"): on the VM204 an ON during a pulse is undone by the
/// pulse's end. A pulse runs until its length has passed, and after that while the board still shows the relay on,
/// for at most <see cref="RelayControl.PulseOffWait"/> (a polled board shows the end a poll or two later). In memory only:
/// a pulse is at most <see cref="RelayControl.MaxPulse"/>, and after a restart nothing is locked. Thread-safe.
/// </summary>
public sealed class AppPulses : IDisposable
{
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<int, Pulse> _pulses = [];
    private bool _disposed;

    public AppPulses(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>
    /// A pulse of <paramref name="relays"/> (channels) starts now and lasts <paramref name="length"/>. A pulse of the same
    /// relay before it is replaced. <paramref name="changed"/> is called when the length has passed and when the lock ends
    /// at the latest, so pages can show the relay as it is again.
    /// </summary>
    public void Start(ChannelMask relays, TimeSpan length, Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(length, TimeSpan.Zero);
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            var end = _time.GetUtcNow() + length;
            foreach (var channel in relays.Channels)
            {
                Remove(channel);
                var pulse = new Pulse(end);
                _pulses[channel] = pulse;
                pulse.Ended = _time.CreateTimer(_ => Notify(changed), null, length, Timeout.InfiniteTimeSpan);
                pulse.Expired = _time.CreateTimer(_ => Expire(channel, pulse, changed), null, length + RelayControl.PulseOffWait, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>Forgets the pulses of <paramref name="relays"/>: one that didn't go out.</summary>
    public void Forget(ChannelMask relays)
    {
        lock (_lock)
        {
            foreach (var channel in relays.Channels)
            {
                Remove(channel);
            }
        }
    }

    /// <summary>Forgets every pulse: the board moved, and another may be there.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var channel in _pulses.Keys.ToList())
            {
                Remove(channel);
            }
        }
    }

    /// <summary>
    /// Whether the app's pulse on <paramref name="channel"/> still runs, with the board showing the relay
    /// <paramref name="shownOn"/>: until its length has passed, then while the relay shows on, for at most
    /// <see cref="RelayControl.PulseOffWait"/>.
    /// </summary>
    public bool IsPulsing(int channel, bool shownOn)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            return _pulses.TryGetValue(channel, out var pulse)
                && (now < pulse.End || (shownOn && now < pulse.End + RelayControl.PulseOffWait));
        }
    }

    /// <summary>The relays in <paramref name="relays"/> whose pulse runs, as <see cref="IsPulsing"/> with <paramref name="state"/>.</summary>
    public ChannelMask Pulsing(ChannelMask relays, BoardState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return ChannelMask.Of([.. relays.Channels.Where(c => IsPulsing(c, state.IsOn(c)))]);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            Clear();
            _disposed = true;
        }
    }

    private void Expire(int channel, Pulse pulse, Action changed)
    {
        lock (_lock)
        {
            if (!_pulses.TryGetValue(channel, out var current) || current != pulse)
            {
                return;
            }

            Remove(channel);
        }

        Notify(changed);
    }

    // Under the lock.
    private void Remove(int channel)
    {
        if (_pulses.Remove(channel, out var pulse))
        {
            pulse.Ended?.Dispose();
            pulse.Expired?.Dispose();
        }
    }

    private static void Notify(Action changed)
    {
        try
        {
            changed();
        }
#pragma warning disable CA1031 // A page's bug must not take down the timer's thread.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Pages draw again at the board's next change.
        }
    }

    private sealed class Pulse
    {
        public Pulse(DateTimeOffset end)
        {
            End = end;
        }

        public DateTimeOffset End { get; }

        public ITimer? Ended { get; set; }

        public ITimer? Expired { get; set; }
    }
}
