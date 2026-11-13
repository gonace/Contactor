using Contactor.Boards;

namespace Contactor.Web.Features.Events;

/// <summary>
/// The relay changes the app itself just asked the board for. When the board's status then shows such a change it
/// is the app's own, not an external one. An expectation lasts 5 s (the OFF at a pulse's end its length plus
/// <c>RelayControl.PulseOffWait</c>): the board answers in well under a second, and a
/// command that changed nothing gets no status at all, so its expectation must not linger and hide a later
/// external change. Expiry is judged by when statuses arrived, not by the clock when one is handled: the recorder may
/// still be behind, and an expectation it hasn't reached yet must not be thrown away by a newer command.
/// </summary>
public sealed class ExpectedChanges
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    // Only a safety limit, e.g. for commands while no status is handled at all: far more than the app ever has waiting.
    private const int MaxExpected = 256;

    private readonly LocalClock _clock;
    private readonly Lock _lock = new();
    private readonly List<(int RelayId, bool On, DateTime ExpiresUtc)> _expected = [];

    public ExpectedChanges(LocalClock clock)
    {
        _clock = clock;
    }

    /// <summary>
    /// Expects every relay in <paramref name="mask"/> to become <paramref name="on"/>, for <see cref="Lifetime"/>, or
    /// <paramref name="lifetime"/> for a change the board makes later by itself, e.g. the end of a pulse.
    /// </summary>
    public void Expect(ChannelMask mask, bool on, TimeSpan? lifetime = null)
    {
        var expires = _clock.UtcNow + (lifetime ?? Lifetime);
        lock (_lock)
        {
            foreach (var relayId in mask.Channels)
            {
                _expected.Add((relayId, on, expires));
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
            foreach (var relayId in mask.Channels)
            {
                var index = _expected.FindLastIndex(e => e.RelayId == relayId && e.On == on);
                if (index >= 0)
                {
                    _expected.RemoveAt(index);
                }
            }
        }
    }

    /// <summary>
    /// Whether a command for this change went out in the last few seconds and the board hasn't shown it yet: the
    /// board's state may still show the relay as it was.
    /// </summary>
    public bool IsPending(int relayId, bool on)
    {
        var now = _clock.UtcNow;
        lock (_lock)
        {
            return _expected.Exists(e => e.RelayId == relayId && e.On == on && e.ExpiresUtc > now);
        }
    }

    /// <summary>
    /// Whether the app asked for this change; if so the expectation is used up, so a second identical change is
    /// external.
    /// </summary>
    /// <param name="relayId">The relay that changed.</param>
    /// <param name="on">What it changed to.</param>
    /// <param name="receivedUtc">
    /// When the status showing the change arrived. The expectation is matched against that, not against when the
    /// status is handled: a status that arrived in time counts even if handling it took longer.
    /// </param>
    public bool TryConsume(int relayId, bool on, DateTime receivedUtc)
    {
        lock (_lock)
        {
            var index = _expected.FindIndex(e => e.RelayId == relayId && e.On == on && e.ExpiresUtc > receivedUtc);
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
}
