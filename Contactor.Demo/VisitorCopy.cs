namespace Contactor.Demo;

/// <summary>Which of a copy's two programs a request is for.</summary>
public enum CopyPart
{
    App,
    Simulator,
}

/// <summary>
/// One visitor's copy: their own app and their own simulator, each a process on 127.0.0.1, with the copy's data in a
/// folder of its own. Made, used and stopped by <see cref="VisitorCopies"/>.
/// </summary>
public sealed class VisitorCopy
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset _lastUsed;
    private int _inUse;

    internal VisitorCopy(string visitorId, string dataFolder, SupervisedProgram app, SupervisedProgram simulator, DateTimeOffset now)
    {
        VisitorId = visitorId;
        DataFolder = dataFolder;
        App = app;
        Simulator = simulator;
        _lastUsed = now;
    }

    /// <summary>The id in the visitor's cookie.</summary>
    public string VisitorId { get; }

    /// <summary>For the log: never the whole id.</summary>
    public string Visitor => VisitorCookie.ForLog(VisitorId);

    public string DataFolder { get; }

    public SupervisedProgram App { get; }

    public SupervisedProgram Simulator { get; }

    /// <summary>Completes once the database is made and both programs are started; throws when that failed.</summary>
    public Task Started => _started.Task;

    /// <summary>When a request last began or ended. Changed under <see cref="VisitorCopies"/>' lock.</summary>
    internal DateTimeOffset LastUsed => _lastUsed;

    /// <summary>Requests and connections (Blazor's live one included) going on now. Changed under the same lock.</summary>
    internal int InUse => _inUse;

    public SupervisedProgram Part(CopyPart part)
    {
        return part == CopyPart.App ? App : Simulator;
    }

    internal void Begin(DateTimeOffset now)
    {
        _inUse++;
        _lastUsed = now;
    }

    internal void End(DateTimeOffset now)
    {
        _inUse--;
        _lastUsed = now;
    }

    internal void Touch(DateTimeOffset now)
    {
        _lastUsed = now;
    }

    internal bool IsIdle(DateTimeOffset now, TimeSpan idleTimeout)
    {
        return _inUse == 0 && now - _lastUsed >= idleTimeout;
    }

    internal void SetStarted()
    {
        _started.TrySetResult();
    }

    internal void SetFailed(Exception exception)
    {
        _started.TrySetException(exception);
    }
}
