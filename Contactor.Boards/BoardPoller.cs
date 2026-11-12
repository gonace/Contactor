namespace Contactor.Boards;

/// <summary>A step of a polled board's connection, for its trace.</summary>
public enum BoardPollStep
{
    /// <summary>Connecting (e.g. logging in); no detail.</summary>
    Connecting,

    /// <summary>The connection failed or was lost; the detail is why.</summary>
    Lost,

    /// <summary>Waiting before the next attempt; the detail is the wait in whole seconds.</summary>
    Waiting,

    /// <summary>The app ended the connection: it is stopping, or the address or login changed. No detail.</summary>
    Stopped,
}

/// <summary>A polled board's state as <see cref="BoardPoller{TReading}"/> keeps it.</summary>
/// <param name="IsConnected">Connected, and the board answered a poll on the current connection.</param>
/// <param name="Reading">The last answer, kept while disconnected; default until the first.</param>
/// <param name="LastContactUtc">When the board last answered a poll, or null if it never has.</param>
public sealed record PolledState<TReading>(bool IsConnected, TReading? Reading, DateTimeOffset? LastContactUtc);

/// <summary>
/// A board that doesn't report changes by itself, as <see cref="BoardPoller{TReading}"/> drives it. The callbacks run
/// on the poller's loop: they must be quick and must not throw.
/// </summary>
public interface IPolledBoard<TReading>
{
    /// <summary>Opens a connection, e.g. logs in. Throws when it can't; the poller tries again later.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Asks the board for its state once. Throws when it can't; that counts as a failed poll.</summary>
    Task<TReading> ReadAsync(CancellationToken cancellationToken);

    /// <summary>The connection or the reading changed (not called for a poll that changed nothing).</summary>
    void OnStateChanged(PolledState<TReading> state);

    /// <summary>A connection step, see <see cref="BoardPollStep"/>.</summary>
    void OnStep(BoardPollStep kind, string? detail);
}

/// <summary>
/// Keeps a board that has to be asked for its state up to date: connects, polls every <see cref="PollInterval"/> and
/// reports the state only when it changed. <see cref="State"/> itself is refreshed on every poll, so its
/// <see cref="PolledState{TReading}.LastContactUtc"/> is always current. After <see cref="FailuresBeforeLost"/> failed
/// polls in a row the board counts as disconnected and the poller connects again, with a wait that doubles from 1 s
/// to 30 s while it keeps failing. Protocol-neutral: the board's own code does the talking.
/// </summary>
public sealed class BoardPoller<TReading>
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    public const int DefaultFailuresBeforeLost = 3;

    private readonly IPolledBoard<TReading> _board;
    private readonly TimeProvider _timeProvider;
    private readonly IEqualityComparer<TReading> _comparer;
    private PolledState<TReading> _state = new(false, default, null);
    private TaskCompletionSource _wake = NewWake();

    // The current attempt (connect, polls and the wait after it); Restart cancels it.
    private CancellationTokenSource? _attempt;
    private int _running;

    /// <param name="board">The board to poll.</param>
    /// <param name="timeProvider">The clock for the waits and <see cref="PolledState{TReading}.LastContactUtc"/>.</param>
    /// <param name="comparer">What counts as a change; e.g. one that ignores a noisy analog value. Default: equality.</param>
    public BoardPoller(IPolledBoard<TReading> board, TimeProvider timeProvider, IEqualityComparer<TReading>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _board = board;
        _timeProvider = timeProvider;
        _comparer = comparer ?? EqualityComparer<TReading>.Default;
    }

    /// <summary>How long between two polls.</summary>
    public TimeSpan PollInterval
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            field = value;
        }
    } = DefaultPollInterval;

    /// <summary>How many failed polls in a row lose the connection; one failure alone may be a hiccup.</summary>
    public int FailuresBeforeLost
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = DefaultFailuresBeforeLost;

    /// <summary>The latest state.</summary>
    public PolledState<TReading> State => Volatile.Read(ref _state);

    /// <summary>Polls at once instead of at the end of the wait, e.g. right after a command so its change shows quickly.</summary>
    public void PollNow()
    {
        Volatile.Read(ref _wake).TrySetResult();
    }

    /// <summary>Ends the current connection, attempt or wait, and connects again at once, e.g. with a new address.</summary>
    public void Restart()
    {
        try
        {
            Volatile.Read(ref _attempt)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // That attempt had just ended; the next one starts anyway.
        }
    }

    /// <summary>Connects and polls until <paramref name="cancellationToken"/> is cancelled. Call it once.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            throw new InvalidOperationException("The poller is already running.");
        }

        try
        {
            var backoff = MinBackoff;
            while (!cancellationToken.IsCancellationRequested)
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Volatile.Write(ref _attempt, attempt);
                try
                {
                    var answered = await RunSessionAsync(attempt.Token).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    // Restarted: straight to the new address or login, no wait.
                    if (attempt.IsCancellationRequested)
                    {
                        backoff = MinBackoff;
                        continue;
                    }

                    if (answered)
                    {
                        backoff = MinBackoff;
                    }

                    _board.OnStep(BoardPollStep.Waiting, ((int)backoff.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    try
                    {
                        await Task.Delay(backoff, _timeProvider, attempt.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        backoff = MinBackoff;
                        continue;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    backoff = backoff * 2 > MaxBackoff ? MaxBackoff : backoff * 2;
                }
                finally
                {
                    Volatile.Write(ref _attempt, null);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>One connection, from connect until it is lost or ended. Returns whether the board answered a poll.</summary>
    private async Task<bool> RunSessionAsync(CancellationToken cancellationToken)
    {
        var answered = false;
        try
        {
            _board.OnStep(BoardPollStep.Connecting, null);
            await _board.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var failures = 0;
            while (true)
            {
                try
                {
                    var reading = await _board.ReadAsync(cancellationToken).ConfigureAwait(false);
                    failures = 0;
                    answered = true;
                    Apply(reading);
                }
#pragma warning disable CA1031 // Whatever a poll throws counts as a failed poll; the loop must go on.
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
                {
                    failures++;
                    if (failures >= FailuresBeforeLost)
                    {
                        _board.OnStep(BoardPollStep.Lost, ex.Message);
                        return answered;
                    }
                }

                await WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _board.OnStep(BoardPollStep.Stopped, null);
        }
#pragma warning disable CA1031 // A failed connect is logged by the board and tried again; the loop must go on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _board.OnStep(BoardPollStep.Lost, ex.Message);
        }
        finally
        {
            SetDisconnected();
        }

        return answered;
    }

    // Until the next poll is due, or PollNow.
    private async Task WaitAsync(CancellationToken cancellationToken)
    {
        var wake = Volatile.Read(ref _wake);
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var delay = Task.Delay(PollInterval, _timeProvider, waiting.Token);
            await Task.WhenAny(delay, wake.Task).ConfigureAwait(false);
            await waiting.CancelAsync().ConfigureAwait(false);
        }

        if (wake.Task.IsCompleted)
        {
            // Used up: the next PollNow wakes the next wait. A PollNow that comes after this is not lost: the poll
            // that follows starts after it.
            Interlocked.CompareExchange(ref _wake, NewWake(), wake);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private void Apply(TReading reading)
    {
        var before = State;
        var after = new PolledState<TReading>(true, reading, _timeProvider.GetUtcNow());
        Volatile.Write(ref _state, after);
        if (!before.IsConnected || before.Reading is null || !_comparer.Equals(before.Reading, reading))
        {
            _board.OnStateChanged(after);
        }
    }

    private void SetDisconnected()
    {
        var state = State;
        if (state.IsConnected)
        {
            var after = state with { IsConnected = false };
            Volatile.Write(ref _state, after);
            _board.OnStateChanged(after);
        }
    }

    private static TaskCompletionSource NewWake()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
