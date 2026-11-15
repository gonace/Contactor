namespace Contactor.Demo;

/// <summary>Which of a copy's two programs a request is for.</summary>
public enum CopyPart
{
    App,
    Simulator,
}

/// <summary>What went wrong with a copy, which the visitor is told rather than that it ended.</summary>
public enum CopyFailure
{
    /// <summary>Nothing: it runs, or it ended.</summary>
    None,

    /// <summary>It never got going: making it failed, or one of its programs was given up before both had listened.</summary>
    DidntStart,

    /// <summary>It got going, but one of its programs was given up later: it exited too often, or didn't come back.</summary>
    WentWrong,
}

/// <summary>
/// One visitor's copy: their own app and their own simulator, each a process on 127.0.0.1, with the copy's data in a
/// folder of its own. Made, used and stopped by <see cref="VisitorCopies"/>.
/// </summary>
public sealed class VisitorCopy
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset _lastActive;

    internal VisitorCopy(
        string visitorId,
        string dataFolder,
        TimeZoneInfo timeZone,
        SupervisedProgram app,
        SupervisedProgram simulator,
        DateTimeOffset now)
    {
        VisitorId = visitorId;
        DataFolder = dataFolder;
        TimeZone = timeZone;
        App = app;
        Simulator = simulator;
        StartedAt = now;
        _lastActive = now;
    }

    /// <summary>The id in the visitor's cookie.</summary>
    public string VisitorId { get; }

    /// <summary>For the log: never the whole id.</summary>
    public string Visitor => VisitorCookie.ForLog(VisitorId);

    public string DataFolder { get; }

    /// <summary>The visitor's time zone (<see cref="VisitorTimeZone"/>), which the copy runs in from its start to its end.</summary>
    public TimeZoneInfo TimeZone { get; }

    public SupervisedProgram App { get; }

    public SupervisedProgram Simulator { get; }

    /// <summary>When the copy was made: it is deleted <see cref="DemoOptions.MaxLifetime"/> later whatever happens.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Completes once the database is made and both programs are started; throws when that failed.</summary>
    public Task Started => _started.Task;

    /// <summary>Completes once the copy is being stopped: no request reaches it any more.</summary>
    public Task Stopped => _stopped.Task;

    /// <summary>
    /// Whether it failed (<see cref="CopyFailure"/>): making it failed, or one of its programs was given up. Then the copy
    /// is stopped too, but the visitor is told it didn't start, or that something went wrong with it once it had got
    /// going (both its programs had listened), rather than that it ended, whichever of the two a request heard of first.
    /// </summary>
    public CopyFailure Failure
    {
        get
        {
            if (_started.Task.Exception?.InnerException is not null and not OperationCanceledException)
            {
                return CopyFailure.DidntStart;
            }

            if (!App.GaveUp.IsCompleted && !Simulator.GaveUp.IsCompleted)
            {
                return CopyFailure.None;
            }

            return App.HasListened && Simulator.HasListened ? CopyFailure.WentWrong : CopyFailure.DidntStart;
        }
    }

    /// <summary>
    /// When the visitor last clicked or pressed a key in either frame, as the page reports it, or loaded the page.
    /// Changed under <see cref="VisitorCopies"/>' lock.
    /// </summary>
    internal DateTimeOffset LastActive => _lastActive;

    /// <summary>
    /// Whether the page still offers "Skip setup": from the copy's start, a new installation whose app opens on the setup
    /// guide, until the visitor chooses the template or the setup. Changed under <see cref="VisitorCopies"/>' lock.
    /// </summary>
    internal bool OffersTemplate { get; private set; } = true;

    public SupervisedProgram Part(CopyPart part)
    {
        return part == CopyPart.App ? App : Simulator;
    }

    internal void Active(DateTimeOffset now)
    {
        if (now > _lastActive)
        {
            _lastActive = now;
        }
    }

    /// <summary>
    /// Whether the copy is done: no click or key for <paramref name="idle"/> (the timeout and the page's count down
    /// together), or older than <paramref name="maxLifetime"/>.
    /// </summary>
    internal bool HasExpired(DateTimeOffset now, TimeSpan idle, TimeSpan maxLifetime)
    {
        return now - _lastActive >= idle || now - StartedAt >= maxLifetime;
    }

    /// <summary>Takes the offer away; false when it was gone already, so only one choice ever acts on it.</summary>
    internal bool TakeTemplateOffer()
    {
        var offered = OffersTemplate;
        OffersTemplate = false;
        return offered;
    }

    internal void SetStarted()
    {
        _started.TrySetResult();
    }

    internal void SetFailed(Exception exception)
    {
        _started.TrySetException(exception);
    }

    internal void SetStopped()
    {
        _stopped.TrySetResult();
    }
}
