using Contactor.Boards;

namespace Contactor.Web.Features.Relays;

/// <summary>
/// The pulses the app started on one board (<c>LiveBoard.Pulses</c>), by channel: the one place that says whether a
/// relay's ON and OFF are locked. While a pulse the app started runs, nobody switches the relay by hand
/// (<see cref="RelayControl"/> refuses, and the pages show "Pulsing…"): on the VM204 an ON during a pulse is undone by the
/// pulse's end.
/// <list type="bullet">
/// <item>A pulse runs until its length has passed. After that only while the board has shown the relay on without a
/// break since then, for at most <see cref="RelayControl.PulseOffWait"/> (a polled board shows the end a poll or two
/// later). Once the board shows it off after the length, at the end itself, in a status (<see cref="Observe"/>) or when
/// asked, the pulse is over for good: a person's ON after that is never taken for the pulse.</item>
/// <item>A pulse starts (<see cref="TryStart"/>) and a press is held (<see cref="TryHold"/>) only after the same check,
/// under one lock: a pulse never starts while a press of that relay is on its way, and a press never goes while its
/// pulse runs.</item>
/// </list>
/// In memory only: a pulse is at most <see cref="RelayControl.MaxPulse"/>, and after a restart nothing is locked.
/// Thread-safe.
/// </summary>
public sealed class AppPulses : IDisposable
{
    private readonly TimeProvider _time;
    private readonly Func<BoardState> _state;
    private readonly Lock _lock = new();
    private readonly Dictionary<int, Pulse> _pulses = [];

    // Presses on their way, by channel: how many.
    private readonly Dictionary<int, int> _holds = [];
    private bool _disposed;

    /// <param name="time">The clock for the pulses' ends.</param>
    /// <param name="state">The board's latest state: whether it shows a relay off once a pulse's length has passed.</param>
    public AppPulses(TimeProvider time, Func<BoardState> state)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(state);
        _time = time;
        _state = state;
    }

    /// <summary>
    /// Starts a pulse of <paramref name="relays"/> (channels), lasting <paramref name="length"/>, unless one of them is
    /// pulsing already or a press of it is on its way: then null, and nothing is started. Otherwise the pulse, to
    /// <see cref="Forget"/> should it not go out. <paramref name="changed"/> is called when the length has passed and when
    /// the lock ends at the latest, so pages can show the relay as it is again.
    /// </summary>
    public object? TryStart(ChannelMask relays, TimeSpan length, Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(length, TimeSpan.Zero);
        lock (_lock)
        {
            if (_disposed || IsLockedLocked(relays) || relays.Channels.Any(_holds.ContainsKey))
            {
                return null;
            }

            var pulse = new Pulse(relays, _time.GetUtcNow() + length);
            foreach (var channel in relays.Channels)
            {
                _pulses[channel] = pulse;
            }

            pulse.Ended = _time.CreateTimer(_ => End(changed), null, length, Timeout.InfiniteTimeSpan);
            pulse.Expired = _time.CreateTimer(_ => Expire(pulse, changed), null, length + RelayControl.PulseOffWait, Timeout.InfiniteTimeSpan);
            return pulse;
        }
    }

    /// <summary>Forgets a pulse <see cref="TryStart"/> started that didn't go out; another pulse is never touched.</summary>
    public void Forget(object pulse)
    {
        ArgumentNullException.ThrowIfNull(pulse);
        lock (_lock)
        {
            if (pulse is Pulse own)
            {
                Remove(own);
            }
        }
    }

    /// <summary>
    /// Holds a press of <paramref name="relays"/> while it is sent, unless one of them is pulsing: then null. While held,
    /// no pulse of them starts. Dispose the hold once the press went out or failed.
    /// </summary>
    public IDisposable? TryHold(ChannelMask relays)
    {
        lock (_lock)
        {
            if (IsLockedLocked(relays))
            {
                return null;
            }

            foreach (var channel in relays.Channels)
            {
                _holds[channel] = _holds.GetValueOrDefault(channel) + 1;
            }

            return new Hold(this, relays);
        }
    }

    /// <summary>Whether the app's pulse on <paramref name="channel"/> still runs, as the board's latest state shows it.</summary>
    public bool IsPulsing(int channel)
    {
        lock (_lock)
        {
            return IsLockedLocked(ChannelMask.Of(channel));
        }
    }

    /// <summary>
    /// A state the board reported: a relay it shows off after its pulse's length has passed has ended its pulse for good.
    /// </summary>
    public void Observe(BoardState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_lock)
        {
            EndShownOffLocked(state);
        }
    }

    /// <summary>Forgets every pulse: the board moved, and another may be there.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var pulse in _pulses.Values.Distinct().ToList())
            {
                Remove(pulse);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            Clear();
            _disposed = true;
        }
    }

    // Under the lock: whether a relay of the mask still pulses. A pulse whose length has passed and whose relay the board
    // shows off is over, and is dropped here.
    private bool IsLockedLocked(ChannelMask relays)
    {
        EndShownOffLocked(_state());
        var now = _time.GetUtcNow();
        return relays.Channels.Any(c => _pulses.TryGetValue(c, out var pulse) && now < pulse.End + RelayControl.PulseOffWait);
    }

    // Under the lock: a relay shown off once its pulse's length has passed ends that pulse for that relay.
    private void EndShownOffLocked(BoardState state)
    {
        var now = _time.GetUtcNow();
        foreach (var (channel, pulse) in _pulses.ToList())
        {
            if (now >= pulse.End && state.IsConnected && !state.IsOn(channel))
            {
                _pulses.Remove(channel);
                if (!_pulses.ContainsValue(pulse))
                {
                    pulse.Dispose();
                }
            }
        }
    }

    private void End(Action changed)
    {
        lock (_lock)
        {
            EndShownOffLocked(_state());
        }

        Notify(changed);
    }

    private void Expire(Pulse pulse, Action changed)
    {
        lock (_lock)
        {
            Remove(pulse);
        }

        Notify(changed);
    }

    // Under the lock.
    private void Remove(Pulse pulse)
    {
        foreach (var channel in pulse.Relays.Channels)
        {
            if (_pulses.TryGetValue(channel, out var current) && ReferenceEquals(current, pulse))
            {
                _pulses.Remove(channel);
            }
        }

        pulse.Dispose();
    }

    private void Release(ChannelMask relays)
    {
        lock (_lock)
        {
            foreach (var channel in relays.Channels)
            {
                if (_holds.GetValueOrDefault(channel) <= 1)
                {
                    _holds.Remove(channel);
                }
                else
                {
                    _holds[channel]--;
                }
            }
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

    private sealed class Pulse : IDisposable
    {
        public Pulse(ChannelMask relays, DateTimeOffset end)
        {
            Relays = relays;
            End = end;
        }

        public ChannelMask Relays { get; }

        public DateTimeOffset End { get; }

        public ITimer? Ended { get; set; }

        public ITimer? Expired { get; set; }

        public void Dispose()
        {
            Ended?.Dispose();
            Expired?.Dispose();
        }
    }

    private sealed class Hold : IDisposable
    {
        private readonly AppPulses _owner;
        private readonly ChannelMask _relays;
        private int _released;

        public Hold(AppPulses owner, ChannelMask relays)
        {
            _owner = owner;
            _relays = relays;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _owner.Release(_relays);
            }
        }
    }
}
