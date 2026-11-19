namespace Contactor.Boards;

/// <summary>
/// What a connection does after the board refused its login (Issue #522), shared by <see cref="BoardPoller{TReading}"/>
/// and the boards with a loop of their own (the VM201), so the rule can't differ between them. A login that never
/// connected since the last new address or login is held. One that connected before is tried every
/// <see cref="BoardPoller{TReading}.AskedToWaitPause"/> for <see cref="BoardPoller{TReading}.RefusedLoginRetries"/>
/// from the first refusal, and then held. Not thread-safe: the owner calls it under its own lock.
/// </summary>
public sealed class RefusedLogin
{
    private bool _worked;
    private DateTimeOffset? _firstRefusalUtc;

    /// <summary>A new address or login: it counts as one that never connected, until it does.</summary>
    public void Forget()
    {
        _worked = false;
        _firstRefusalUtc = null;
    }

    /// <summary>The login connected: it works, and the time of its refusals starts over.</summary>
    public void Worked()
    {
        _worked = true;
        _firstRefusalUtc = null;
    }

    /// <summary>
    /// The wait before the next try after the board refused the login at <paramref name="now"/>:
    /// <see cref="Timeout.InfiniteTimeSpan"/> to hold it until a new login or a person's "Check again".
    /// </summary>
    public TimeSpan WaitAfterRefusal(DateTimeOffset now)
    {
        if (!_worked)
        {
            return Timeout.InfiniteTimeSpan;
        }

        _firstRefusalUtc ??= now;
        return now - _firstRefusalUtc.Value >= BoardPoller<int>.RefusedLoginRetries
            ? Timeout.InfiniteTimeSpan
            : BoardPoller<int>.AskedToWaitPause;
    }
}
