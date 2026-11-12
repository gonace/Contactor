namespace Contactor.Web.Features.Events;

/// <summary>
/// Since when the app has watched the board without a break: from the first status after the app started, or after
/// contact came back. A change it logged after that is one it saw happen. Before it, or at that moment (a change made
/// while the app wasn't watching is logged when it notices), the time is unknown: the board only reports what it
/// reads now. Kept by <see cref="BoardEventRecorder"/>.
/// </summary>
public sealed class BoardWatch
{
    private readonly Lock _gate = new();
    private DateTime? _sinceUtc;

    /// <summary>Null while the app has no contact with the board.</summary>
    public DateTime? WatchingSinceUtc
    {
        get
        {
            lock (_gate)
            {
                return _sinceUtc;
            }
        }
    }

    /// <summary>Whether the app saw a change logged at <paramref name="changeUtc"/> happen.</summary>
    public bool SawChangeAt(DateTime changeUtc)
    {
        return WatchingSinceUtc is { } since && changeUtc > since;
    }

    /// <summary>The board answered: watching from <paramref name="utc"/> on, unless it already was.</summary>
    public void Started(DateTime utc)
    {
        lock (_gate)
        {
            _sinceUtc ??= utc;
        }
    }

    /// <summary>Contact with the board was lost.</summary>
    public void Stopped()
    {
        lock (_gate)
        {
            _sinceUtc = null;
        }
    }
}
