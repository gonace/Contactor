using Contactor.Boards;

namespace Contactor.Web.Features.Events;

/// <summary>
/// The relay changes the app itself just asked one board for, by channel: each live board has its own
/// (<c>LiveBoard.Expected</c>). When the board's status then shows such a change it
/// is the app's own, not an external one. An expectation lasts 5 s (the OFF at a pulse's end its length plus
/// <c>RelayControl.PulseOffWait</c>): the board answers in well under a second, and a
/// command that changed nothing gets no status at all, so its expectation must not linger and hide a later
/// external change. Expiry is judged by when statuses arrived, not by the clock when one is handled: the recorder may
/// still be behind, and an expectation it hasn't reached yet must not be thrown away by a newer command.
/// An expectation is on its way (<see cref="IsPending"/>) until a status shows the change (<see cref="Observe"/>, called
/// by <c>LiveBoard</c> before it raises the status), and the recorder uses it up (<see cref="TryConsume"/>) when it
/// handles that status, which may be later.
/// </summary>
public sealed class ExpectedChanges
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    // Only a safety limit, e.g. for commands while no status is handled at all: far more than the app ever has waiting.
    private const int MaxExpected = 256;

    private readonly Func<DateTime> _utcNow;
    private readonly Lock _lock = new();
    private readonly List<Entry> _expected = [];

    // The relays of the last connected status observed; null at start and once the board's state is forgotten.
    private ChannelMask? _shown;

    public ExpectedChanges(LocalClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _utcNow = () => clock.UtcNow;
    }

    public ExpectedChanges(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _utcNow = () => time.GetUtcNow().UtcDateTime;
    }

    /// <summary>
    /// Expects every relay (channel) in <paramref name="mask"/> to become <paramref name="on"/>, for <see cref="Lifetime"/>, or
    /// <paramref name="lifetime"/> for a change the board makes later by itself, e.g. the end of a pulse.
    /// </summary>
    public void Expect(ChannelMask mask, bool on, TimeSpan? lifetime = null)
    {
        var expires = _utcNow() + (lifetime ?? Lifetime);
        lock (_lock)
        {
            foreach (var channel in mask.Channels)
            {
                _expected.Add(new Entry(channel, on, expires, Seen: false));
            }

            if (_expected.Count > MaxExpected)
            {
                _expected.RemoveRange(0, _expected.Count - MaxExpected);
            }
        }
    }

    /// <summary>
    /// Drops what <see cref="Expect"/> added for a command that then failed (refused, unreadable, no answer). The
    /// board may still have switched; the status that shows it is then an external change, logged as one, instead of
    /// using the expectation up as the app's own while the app recorded nothing. Drops the newest matching
    /// expectation for each relay, the one the failed command added. Call it first thing when the command throws: it
    /// must run before the status from the read straight after a failed command (a polled board reads at once) reaches
    /// <see cref="BoardEventRecorder"/>, or that status uses the expectation up silently and the change is lost
    /// instead of logged as external. It does in practice: the read starts only once the command lets go of the board
    /// and then goes to the board and back, while the exception reaches the caller at once.
    /// </summary>
    public void Forget(ChannelMask mask, bool on)
    {
        lock (_lock)
        {
            foreach (var channel in mask.Channels)
            {
                var index = _expected.FindLastIndex(e => e.Channel == channel && e.On == on);
                if (index >= 0)
                {
                    _expected.RemoveAt(index);
                }
            }
        }
    }

    /// <summary>
    /// Drops every expectation: the board moved to a new address, where another board may be, and a change there is not
    /// the app's own.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _expected.Clear();
            _shown = null;
        }
    }

    /// <summary>
    /// Adds an expectation the board has already shown, for the usual <see cref="Lifetime"/>: one the recorder used up
    /// for a status it failed to save, so the next try matches it again. It is not on its way.
    /// </summary>
    public void ExpectAgain(int channel, bool on)
    {
        var expires = _utcNow() + Lifetime;
        lock (_lock)
        {
            _expected.Add(new Entry(channel, on, expires, Seen: true));
            if (_expected.Count > MaxExpected)
            {
                _expected.RemoveRange(0, _expected.Count - MaxExpected);
            }
        }
    }

    /// <summary>
    /// Takes in a status the board raised, before anything else handles it: each relay it shows change since the last
    /// connected status marks the expectation for that change as shown, the same one <see cref="TryConsume"/> takes
    /// for it, so it is no longer on its way. Only a change marks one, never a status that merely shows the relay that
    /// way: a status from before the command shows the relay as it was. The first status after start or after the
    /// board's state was forgotten marks nothing. A lost connection keeps the last status, as the recorder does.
    /// </summary>
    public void Observe(BoardState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_lock)
        {
            if (!state.IsConnected)
            {
                if (state.LastContactUtc is null)
                {
                    _shown = null;
                }

                return;
            }

            if (_shown is { } shown)
            {
                var received = state.LastContactUtc?.UtcDateTime ?? _utcNow();
                foreach (var channel in (shown.Except(state.Relays) | state.Relays.Except(shown)).Channels)
                {
                    var on = state.Relays.Contains(channel);
                    var index = _expected.FindIndex(e => !e.Seen && e.Channel == channel && e.On == on && e.ExpiresUtc > received);
                    if (index >= 0)
                    {
                        _expected[index] = _expected[index] with { Seen = true };
                    }
                }
            }

            _shown = state.Relays;
        }
    }

    /// <summary>
    /// Whether a command for this change went out in the last few seconds and the board hasn't shown it yet: the
    /// board's state may still show the relay as it was. Read it before the board's state, never after: a status that
    /// comes in between then shows in the state. Read after, it can end what is on its way while the state read
    /// before still shows the relay as it was.
    /// </summary>
    public bool IsPending(int channel, bool on)
    {
        var now = _utcNow();
        lock (_lock)
        {
            return _expected.Exists(e => !e.Seen && e.Channel == channel && e.On == on && e.ExpiresUtc > now);
        }
    }

    /// <summary>
    /// Whether the app asked for this change; if so the expectation is used up, so a second identical change is
    /// external. One a status showed (<see cref="Observe"/>) first, so the recorder takes the same one.
    /// </summary>
    /// <param name="channel">The relay's channel that changed.</param>
    /// <param name="on">What it changed to.</param>
    /// <param name="receivedUtc">
    /// When the status showing the change arrived. The expectation is matched against that, not against when the
    /// status is handled: a status that arrived in time counts even if handling it took longer.
    /// </param>
    public bool TryConsume(int channel, bool on, DateTime receivedUtc)
    {
        lock (_lock)
        {
            var index = _expected.FindIndex(e => e.Seen && e.Channel == channel && e.On == on && e.ExpiresUtc > receivedUtc);
            if (index < 0)
            {
                index = _expected.FindIndex(e => e.Channel == channel && e.On == on && e.ExpiresUtc > receivedUtc);
            }

            if (index >= 0)
            {
                _expected.RemoveAt(index);
            }

            // Statuses are handled in the order they arrived, so one that had expired when this status arrived can't
            // match a later one.
            _expected.RemoveAll(e => e.ExpiresUtc <= receivedUtc);
            return index >= 0;
        }
    }

    // Seen: a status showed the change, so it is no longer on its way; the recorder still uses it up.
    private readonly record struct Entry(int Channel, bool On, DateTime ExpiresUtc, bool Seen);
}
