using Contactor.Boards;

namespace Contactor.Web.Features.Events;

/// <summary>
/// The relay changes the app itself just asked the board for. When the board's status then shows such a change it
/// is the app's own, not an external one. An expectation lasts 5 s: the board answers in well under a second, and a
/// command that changed nothing gets no status at all, so its expectation must not linger and hide a later
/// external change. Expiry is judged by when statuses arrived, not by the clock when one is handled: the recorder may
/// still be behind, and an expectation it hasn't reached yet must not be thrown away by a newer command.
/// </summary>
public sealed class ExpectedChanges
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a failed ON put back after a failed save waits for the next status: long enough for a board that only
    /// reports at its next change or keep-alive (the VM201 every 15 s).
    /// </summary>
    public static readonly TimeSpan RetryLifetime = TimeSpan.FromMinutes(1);

    // Only a safety limit, e.g. for commands while no status is handled at all: far more than the app ever has waiting.
    private const int MaxExpected = 256;

    private readonly LocalClock _clock;
    private readonly Lock _lock = new();
    private readonly List<(int RelayId, bool On, DateTime ExpiresUtc)> _expected = [];

    // ONs from a person or an input whose command failed, for a relay the board may have switched on all the same.
    private readonly List<FailedOn> _failedOn = [];

    public ExpectedChanges(LocalClock clock)
    {
        _clock = clock;
    }

    /// <summary>Expects every relay in <paramref name="mask"/> to become <paramref name="on"/>.</summary>
    public void Expect(ChannelMask mask, bool on)
    {
        var expires = _clock.UtcNow + Lifetime;
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
    /// Remembers, for <see cref="Lifetime"/>, that <paramref name="actor"/> (a person or an input) asked for the relay
    /// to come on and the command was reported as failed. If the board switched it on anyway, the status that shows
    /// it is credited to them (<see cref="TakeFailedOn"/>), so the relay gets their auto-off.
    /// </summary>
    public void RememberFailedOn(int relayId, string actor)
    {
        Add(new FailedOn(relayId, actor, _clock.UtcNow + Lifetime));
    }

    /// <summary>
    /// Puts back a note <see cref="TakeFailedOn"/> took, when recording the change failed. The change is recorded
    /// again only with the next status, so the note lasts at least <see cref="RetryLifetime"/> from now.
    /// </summary>
    public void PutBackFailedOn(FailedOn note)
    {
        ArgumentNullException.ThrowIfNull(note);
        var retryUntil = _clock.UtcNow + RetryLifetime;
        Add(note.ExpiresUtc > retryUntil ? note : note with { ExpiresUtc = retryUntil });
    }

    private void Add(FailedOn note)
    {
        lock (_lock)
        {
            _failedOn.Add(note);
            if (_failedOn.Count > MaxExpected)
            {
                _failedOn.RemoveRange(0, _failedOn.Count - MaxExpected);
            }
        }
    }

    /// <summary>
    /// The newest failed ON for this relay that hadn't expired when the status showing it on arrived, if any; it is used
    /// up. Notes that had expired by then are dropped.
    /// </summary>
    public FailedOn? TakeFailedOn(int relayId, DateTime receivedUtc)
    {
        lock (_lock)
        {
            var index = _failedOn.FindLastIndex(f => f.RelayId == relayId && f.ExpiresUtc > receivedUtc);
            var note = index >= 0 ? _failedOn[index] : null;
            if (index >= 0)
            {
                _failedOn.RemoveAt(index);
            }

            _failedOn.RemoveAll(f => f.ExpiresUtc <= receivedUtc);
            return note;
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

/// <summary>An ON from a person or an input whose command was reported as failed, until <paramref name="ExpiresUtc"/>.</summary>
public sealed record FailedOn(int RelayId, string Actor, DateTime ExpiresUtc);
