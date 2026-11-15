namespace Contactor.Demo;

/// <summary>
/// Keeps one of a copy's programs running: when it exits by itself (the app does after the setup guide's board
/// choice), it is started again with the same port, environment and data, as compose does on the Pi: at once the first
/// <see cref="RestartsAtOnce"/> times in <see cref="RestartWindow"/>, then after a wait that doubles each time, up to
/// <see cref="MaxRestartWait"/>. A program that exits before it ever listens, <see cref="MaxFailedStarts"/> times in a
/// row, that doesn't listen within the start timeout, or that exits <see cref="MaxRestarts"/> times in
/// <see cref="RestartWindow"/>, is given up: <see cref="GaveUp"/> completes and the copy is stopped.
/// </summary>
public sealed partial class SupervisedProgram : IAsyncDisposable
{
    /// <summary>How many starts in a row may end before the program listens.</summary>
    public const int MaxFailedStarts = 3;

    /// <summary>How many restarts in <see cref="RestartWindow"/> happen at once, without a wait.</summary>
    public const int RestartsAtOnce = 3;

    /// <summary>How many restarts in <see cref="RestartWindow"/> are made at most; the next exit gives the program up.</summary>
    public const int MaxRestarts = 10;

    /// <summary>The time the restarts are counted in.</summary>
    public static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(10);

    /// <summary>The longest wait before a restart.</summary>
    public static readonly TimeSpan MaxRestartWait = TimeSpan.FromSeconds(30);

    private readonly ProgramStart _start;
    private readonly IProgramStarter _starter;
    private readonly TimeSpan _startTimeout;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _gaveUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _loop = Task.CompletedTask;
    private int _starts;
    private int _stopped;

    public SupervisedProgram(ProgramStart start, IProgramStarter starter, TimeSpan startTimeout, TimeProvider time, ILogger logger)
    {
        _start = start;
        _starter = starter;
        _startTimeout = startTimeout;
        _time = time;
        _logger = logger;
    }

    public ProgramStart Program => _start;

    /// <summary>How many times it was started, the first included.</summary>
    public int Starts => Volatile.Read(ref _starts);

    /// <summary>Completes when it was given up; never when it is stopped.</summary>
    public Task GaveUp => _gaveUp.Task;

    /// <summary>
    /// Completes once the program listens: at once while it runs, after the restart while it is being started again.
    /// Throws once it was given up or stopped.
    /// </summary>
    public Task Listening => Volatile.Read(ref _listening).Task;

    public void Start()
    {
        _loop = RunAsync(_stop.Token);
    }

    /// <summary>Stops the program and everything it started, and waits until it has exited.</summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            await _stop.CancelAsync();
        }

        await _loop;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stop.Dispose();
    }

    /// <summary>
    /// How long to wait before the next restart, after <paramref name="earlier"/> restarts in
    /// <see cref="RestartWindow"/>: none for the first <see cref="RestartsAtOnce"/>, then 1, 2, 4… seconds, at most
    /// <see cref="MaxRestartWait"/>.
    /// </summary>
    public static TimeSpan RestartWait(int earlier)
    {
        if (earlier < RestartsAtOnce)
        {
            return TimeSpan.Zero;
        }

        var doublings = Math.Min(earlier - RestartsAtOnce, 10);
        var wait = TimeSpan.FromSeconds(1 << doublings);
        return wait < MaxRestartWait ? wait : MaxRestartWait;
    }

    private async Task RunAsync(CancellationToken stop)
    {
        var failedStarts = 0;
        Queue<DateTimeOffset> restarts = [];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                Interlocked.Increment(ref _starts);
                var listened = await RunOnceAsync(stop);
                if (stop.IsCancellationRequested)
                {
                    return;
                }

                failedStarts = listened ? 0 : failedStarts + 1;
                if (failedStarts >= MaxFailedStarts)
                {
                    LogGaveUp(_logger, _start.Name, _start.Visitor, failedStarts);
                    GiveUp(new InvalidOperationException($"The {_start.Name} doesn't start."));
                    return;
                }

                var now = _time.GetUtcNow();
                while (restarts.Count > 0 && now - restarts.Peek() >= RestartWindow)
                {
                    restarts.Dequeue();
                }

                if (restarts.Count >= MaxRestarts)
                {
                    LogGaveUpRestarts(_logger, _start.Name, _start.Visitor, restarts.Count, RestartWindow);
                    GiveUp(new InvalidOperationException($"The {_start.Name} exits too often."));
                    return;
                }

                var wait = RestartWait(restarts.Count);
                LogRestarting(_logger, _start.Name, _start.Visitor, wait);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _time, stop);
                }

                restarts.Enqueue(_time.GetUtcNow());
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Stopped while it waited to start again.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(_logger, _start.Name, _start.Visitor, ex);
            GiveUp(ex);
        }
        finally
        {
            if (stop.IsCancellationRequested)
            {
                // Stopped: nothing waits for it any more, also not after it listened.
                var stopped = new TaskCompletionSource();
                stopped.SetCanceled(CancellationToken.None);
                Volatile.Read(ref _listening).TrySetCanceled(CancellationToken.None);
                Volatile.Write(ref _listening, stopped);
            }
        }
    }

    private void GiveUp(Exception exception)
    {
        Volatile.Read(ref _listening).TrySetException(exception);
        _gaveUp.TrySetResult();
    }

    /// <summary>One run, until it exits or is stopped. Returns whether it listened.</summary>
    private async Task<bool> RunOnceAsync(CancellationToken stop)
    {
        var listening = Volatile.Read(ref _listening);
        var run = _starter.Start(_start);
        await using (run)
        {
            using var probing = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var listened = false;
            try
            {
                await run.WaitUntilListeningAsync(probing.Token).WaitAsync(_startTimeout, _time, stop);
                listened = true;
                listening.TrySetResult();
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                await run.StopAsync();
                return false;
            }
            catch (TimeoutException ex)
            {
                // It runs but doesn't listen: no use waiting for it any longer.
                LogNotListening(_logger, _start.Name, _start.Visitor);
                await probing.CancelAsync();
                await run.StopAsync();
                throw new InvalidOperationException($"The {_start.Name} didn't listen within {_startTimeout}.", ex);
            }
            catch (InvalidOperationException)
            {
                // It exited before it listened; Exited says how.
            }

            try
            {
                await run.Exited.WaitAsync(stop);
            }
            catch (OperationCanceledException)
            {
                await run.StopAsync();
                return listened;
            }

            var exitCode = await run.Exited;
            LogExited(_logger, _start.Name, _start.Visitor, exitCode);
            if (listened)
            {
                // Requests from now on wait for the next run to listen.
                Volatile.Write(ref _listening, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            return listened;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The {Program} of visitor {Visitor} exited with code {ExitCode}")]
    private static partial void LogExited(ILogger logger, string program, string visitor, int exitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting the {Program} of visitor {Visitor} again in {Wait}")]
    private static partial void LogRestarting(ILogger logger, string program, string visitor, TimeSpan wait);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gave up the {Program} of visitor {Visitor}: it was started again {Times} times in {Window}")]
    private static partial void LogGaveUpRestarts(ILogger logger, string program, string visitor, int times, TimeSpan window);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Program} of visitor {Visitor} didn't listen in time")]
    private static partial void LogNotListening(ILogger logger, string program, string visitor);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gave up the {Program} of visitor {Visitor}: it exited {Times} times in a row before it listened")]
    private static partial void LogGaveUp(ILogger logger, string program, string visitor, int times);

    [LoggerMessage(Level = LogLevel.Error, Message = "Gave up the {Program} of visitor {Visitor}")]
    private static partial void LogFailed(ILogger logger, string program, string visitor, Exception exception);
}
