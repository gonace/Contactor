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

    /// <summary>
    /// The board refused a login that never connected, or one that connected before for
    /// <see cref="BoardPoller{TReading}.RefusedLoginRetries"/>: the poller sends nothing more until a person saves a new
    /// one or asks to check again (<see cref="BoardPoller{TReading}.TryAgain"/>). No detail.
    /// </summary>
    Held,
}

/// <summary>A polled board's state as <see cref="BoardPoller{TReading}"/> keeps it.</summary>
/// <param name="IsConnected">Connected, and the board answered a poll on the current connection.</param>
/// <param name="Reading">The last answer, kept while disconnected; default until the first.</param>
/// <param name="LastContactUtc">When the board last answered a poll, or null if it never has.</param>
/// <param name="Problem">
/// Why the board refused the last connect on purpose (<see cref="IConnectionProblemSource"/>); null while it answers, and
/// when the last connect failed for another reason.
/// </param>
public sealed record PolledState<TReading>(bool IsConnected, TReading? Reading, DateTimeOffset? LastContactUtc, ConnectionProblem? Problem = null);

/// <summary>
/// A board that doesn't report changes by itself, as <see cref="BoardPoller{TReading}"/> drives it. The poller calls
/// <see cref="ConnectAsync"/> and <see cref="ReadAsync"/> one at a time, never while a command runs. The callbacks run
/// on the poller's loop: they must be quick and must not throw.
/// </summary>
public interface IPolledBoard<TReading>
{
    /// <summary>
    /// Opens a connection, e.g. logs in. Throws when it can't; the poller tries again later. An exception that is an
    /// <see cref="IConnectionProblemSource"/> says why the board refused it, for <see cref="PolledState{TReading}.Problem"/>.
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Asks the board for its state once. Throws when it can't; that counts as a failed poll. When
    /// <paramref name="cancellationToken"/> is cancelled because a command takes the poll's place, it throws
    /// <see cref="OperationCanceledException"/>, which doesn't count.
    /// </summary>
    Task<TReading> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The connection or the reading changed, as the poller's comparer sees it (not called for a poll that changed
    /// nothing).
    /// </summary>
    void OnStateChanged(PolledState<TReading> state);

    /// <summary>
    /// A poll's or command's reading differs from the one before, but not as the poller's comparer sees it, e.g. only a
    /// noisy analog value moved: <see cref="OnStateChanged"/> isn't called for it. Nothing by default.
    /// </summary>
    void OnReadingChanged(PolledState<TReading> state)
    {
    }

    /// <summary>A connection step, see <see cref="BoardPollStep"/>.</summary>
    void OnStep(BoardPollStep kind, string? detail);
}

/// <summary>
/// Keeps a board that has to be asked for its state up to date: connects, polls every <see cref="PollInterval"/> and
/// reports the state only when it changed. <see cref="State"/> itself is refreshed on every poll, so its
/// <see cref="PolledState{TReading}.LastContactUtc"/> is always current. After <see cref="FailuresBeforeLost"/> failed
/// polls in a row the board counts as disconnected and the poller connects again, with a wait that doubles from 1 s
/// to 30 s while it keeps failing. A connect the board refused on purpose isn't tried again in that loop. After a
/// refused login (<see cref="ConnectionProblemKind.LoginRefused"/>) that never connected since the last
/// <see cref="Restart"/>, the poller waits for a new login (<see cref="Restart"/>) or a person's "Check again"
/// (<see cref="TryAgain"/>). A login that connected before is tried every <see cref="AskedToWaitPause"/> for
/// <see cref="RefusedLoginRetries"/> from the first refusal, and then held the same way. After the board asked it to
/// wait (<see cref="ConnectionProblemKind.AskedToWait"/>) it waits <see cref="AskedToWaitPause"/>. Commands go through
/// <see cref="RunCommandAsync"/>: never behind a poll, and never to a board that is lost. Protocol-neutral: the board's own code does the talking.
/// </summary>
public sealed class BoardPoller<TReading> : IDisposable, IAsyncDisposable
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the poller waits by itself after the board asked it to wait. Longer than a Shelly counts wrong passwords
    /// for (10 minutes, Issue #522), so its own wait runs out and the one attempt after can't keep it going.
    /// </summary>
    public static readonly TimeSpan AskedToWaitPause = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a login that connected before is still tried after the board refuses it, once every
    /// <see cref="AskedToWaitPause"/>, counted from the first refusal. After that it is held as a login that never worked.
    /// </summary>
    public static readonly TimeSpan RefusedLoginRetries = TimeSpan.FromHours(24);

    public const int DefaultFailuresBeforeLost = 3;
    public static readonly TimeSpan DefaultDisposeWait = TimeSpan.FromSeconds(15);

    private readonly IPolledBoard<TReading> _board;
    private readonly TimeProvider _timeProvider;
    private readonly IEqualityComparer<TReading> _comparer;

    // One talk with the board at a time: a connect, a poll or a command. Whether the board is connected changes only
    // under it, so a command can't slip in between a poll's last failure and the board counting as lost.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PolledState<TReading> _state = new(false, default, null);

    // Which board the state is from: Forget moves on to the next, so an answer from the one before is dropped. The
    // state is written, and its change reported, under _stateLock, so a dropped answer can't overtake the reset.
    private readonly Lock _stateLock = new();
    private int _generation;

    // Which login the connect was for: Restart moves on to the next, so a problem from the attempt before is dropped.
    // Under _stateLock, as _generation.
    private int _restarts;

    // Whether the login in use connected (a poll answered) since the last Restart, and when the board first refused it
    // after that. Under _stateLock. A login that worked is tried again now and then after a refusal, for at most
    // RefusedLoginRetries (Issue #522): the board may refuse it for a while, e.g. while it restarts.
    private readonly RefusedLogin _refusedLogin = new();
    private TaskCompletionSource _wake = NewWake();

    // The current attempt (connect, polls and the wait after it); Restart cancels it.
    private CancellationTokenSource? _attempt;

    // The poll under way; a command cancels it to go at once.
    private CancellationTokenSource? _poll;

    // Polls and commands that failed, in a row, on this connection; and whether that lost the board. Under _gate.
    private int _failures;
    private bool _lost;
    private int _running;

    // Disposing: no new connect, poll or command, and the run ends. The lock and this source are left to the collector,
    // so a command still waiting on the lock gets BoardNotConnectedException instead of a lock that is gone.
    private readonly CancellationTokenSource _disposing = new();
    private int _disposed;

    /// <param name="board">The board to poll.</param>
    /// <param name="timeProvider">The clock for the waits and <see cref="PolledState{TReading}.LastContactUtc"/>.</param>
    /// <param name="comparer">
    /// What counts as a change; e.g. one that ignores a noisy analog value, which then comes as
    /// <see cref="IPolledBoard{TReading}.OnReadingChanged"/>. Default: equality.
    /// </param>
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

    /// <summary>
    /// How long <see cref="DisposeAsync"/> waits for a connect, poll or command under way to end, so the board's code can
    /// close its connection without one still using it.
    /// </summary>
    public TimeSpan DisposeWait
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            field = value;
        }
    } = DefaultDisposeWait;

    /// <summary>The latest state.</summary>
    public PolledState<TReading> State => Volatile.Read(ref _state);

    /// <summary>Polls at once instead of at the end of the wait. Does nothing while disconnected.</summary>
    public void PollNow()
    {
        if (State.IsConnected)
        {
            Volatile.Read(ref _wake).TrySetResult();
        }
    }

    /// <summary>
    /// Ends the current connection, attempt or wait, and connects again at once, e.g. with a new address or login. The
    /// <see cref="PolledState{TReading}.Problem"/> is cleared, since it was about the last login, and a problem from the
    /// attempt before still on its way is dropped. The login counts as one that never connected, until it does.
    /// </summary>
    public void Restart()
    {
        Restart(newLogin: true);
    }

    private void Restart(bool newLogin)
    {
        lock (_stateLock)
        {
            _restarts++;
            if (newLogin)
            {
                _refusedLogin.Forget();
            }

            var state = _state;
            if (state.Problem is not null)
            {
                var after = state with { Problem = null };
                Volatile.Write(ref _state, after);
                _board.OnStateChanged(after);
            }
        }

        Cancel(ref _attempt);
    }

    /// <summary>
    /// A person's "Check again" while the board isn't connected: as <see cref="Restart"/>, it ends the wait (also the
    /// one after a refused login, which only this or a new login ends) and connects again at once, with the problem
    /// cleared until the board answers again. Unlike <see cref="Restart"/>, the login is the same: whether it worked
    /// before, and since when the board refuses it, are kept. Nothing happens while the board is connected.
    /// </summary>
    public void TryAgain()
    {
        if (!State.IsConnected)
        {
            Restart(newLogin: false);
        }
    }

    /// <summary>
    /// Forgets the board's state, as when the app starts: another board is at a new address, and what the old one
    /// read says nothing about it. An answer from before still on its way is dropped. Call it before
    /// <see cref="Restart"/>.
    /// </summary>
    public void Forget()
    {
        lock (_stateLock)
        {
            _generation++;
            var state = new PolledState<TReading>(false, default, null);
            if (Equals(_state, state))
            {
                // Already nothing known, e.g. the saved address set at start: no change to report.
                return;
            }

            Volatile.Write(ref _state, state);
            _board.OnStateChanged(state);
        }
    }

    /// <summary>
    /// Runs a command on the board at once: a poll under way is given up (it doesn't count as failed) and the command
    /// waits only for it to stop, never for the next one. Throws <see cref="BoardNotConnectedException"/> when the
    /// board is not connected, decided under the same lock as the poller's. What the command returns is the board's
    /// state right after it, taken as a poll's answer, so the change shows without asking again. A command fails as a
    /// poll would: whatever it throws (no answer, an answer it can't read) counts as one failed poll, so a board that
    /// is gone or broken is lost even while commands keep coming. Three things don't count: a cancelled
    /// <paramref name="cancellationToken"/>; <see cref="BoardNotConnectedException"/>, with which the command says it
    /// sent nothing (e.g. the board is logging in again); and <see cref="BoardRefusedException"/>, with which it says
    /// the board answered but refused the command. A refusal starts the count over, as an answered poll does: the
    /// board answered. After a failed command, unless <paramref name="cancellationToken"/> was cancelled, the board's
    /// state is read at once, as by <see cref="PollNow"/>, and the exception is passed on.
    /// </summary>
    public async Task RunCommandAsync(Func<CancellationToken, Task<TReading>> command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await RunSwitchesAsync(async token => new CommandReading<TReading>(await command(token).ConfigureAwait(false)), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// As <see cref="RunCommandAsync"/>, for a command that switches the relays and then reads the board's state, and
    /// whose read may fail once the switches went out. Such a command doesn't fail: it returns a
    /// <see cref="CommandReading{TReading}"/> with no reading (its default value). That isn't an answered poll:
    /// <see cref="State"/> keeps its last reading and its <see cref="PolledState{TReading}.LastContactUtc"/>, the failed
    /// polls in a row aren't started over, and the board's state is read at once, as by <see cref="PollNow"/>, which
    /// counts on its own if it fails too.
    /// </summary>
    public async Task RunSwitchesAsync(Func<CancellationToken, Task<CommandReading<TReading>>> command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (IsDisposed || !State.IsConnected)
        {
            throw new BoardNotConnectedException();
        }

        var generation = Volatile.Read(ref _generation);
        Cancel(ref _poll);
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposing.Token))
        {
            try
            {
                await _gate.WaitAsync(waiting.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Disposed while it waited: it never went out.
                throw new BoardNotConnectedException();
            }
        }

        try
        {
            if (IsDisposed || !State.IsConnected)
            {
                throw new BoardNotConnectedException();
            }

            CommandReading<TReading> reading;
            try
            {
                reading = await command(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsDisposed && !cancellationToken.IsCancellationRequested)
            {
                // Dispose began while it ran: the board's code may close the connection under it (at once after
                // Dispose, or after DisposeWait after DisposeAsync), so it may meet a null or disposed stream. Whatever
                // it throws once Dispose began means "not carried out for sure", as for any board that went away. Not
                // counted: nothing polls any more.
                throw new BoardNotConnectedException("The relay board was closed while the command ran; it may not have been carried out.", ex);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // The same rule as a poll's, counted once, here; the read that follows is an ordinary poll and counts
                // on its own if it fails too. A refusal is an answer: the board is there. Quick commands
                // cancel the reads between them, so counting refusals would lose a board that answers every time.
                if (ex is BoardRefusedException)
                {
                    _failures = 0;
                }
                else if (ex is not BoardNotConnectedException)
                {
                    CountFailure(ex.Message);
                }

                // The change was refused or got no answer: read what the board really did at once, not at the next
                // poll. Nothing to read when that lost the board; the poller is already connecting again.
                PollNow();
                throw;
            }

            if (!reading.IsRead)
            {
                // The switches went out, but the board's state after them wasn't read: no contact, and a read at once.
                PollNow();
                return;
            }

            _failures = 0;
            Apply(reading.Reading!, generation);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops new work at once: the run ends, and a command that comes after, or still waits for the lock, throws
    /// <see cref="BoardNotConnectedException"/>. Doesn't wait for the work under way; <see cref="DisposeAsync"/> does.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        Cancel(ref _poll);
        _disposing.Cancel();
    }

    /// <summary>
    /// As <see cref="Dispose"/>, then waits for the connect, poll or command under way to end, at most
    /// <see cref="DisposeWait"/>, so the board's code can close its connection with nothing still using it. A
    /// command that outlasts the wait throws <see cref="BoardNotConnectedException"/>, whatever the closed connection
    /// makes it throw.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Dispose();
        using var bound = new CancellationTokenSource(DisposeWait, _timeProvider);
        try
        {
            await _gate.WaitAsync(bound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Still under way after DisposeWait: it ends as a command that lost its board.
            return;
        }

        _gate.Release();
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    /// <summary>Connects and polls until <paramref name="cancellationToken"/> is cancelled. Call it once.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            throw new InvalidOperationException("The poller is already running.");
        }

        // Disposing ends the run as the caller's cancel does.
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposing.Token);
        cancellationToken = stopping.Token;
        try
        {
            var backoff = MinBackoff;
            while (!cancellationToken.IsCancellationRequested)
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Volatile.Write(ref _attempt, attempt);
                try
                {
                    var (answered, refused) = await RunSessionAsync(attempt.Token).ConfigureAwait(false);
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

                    // Never a refused login in a loop (Issue #522): each try counts toward a board's lockout, e.g. a
                    // Shelly's brute-force protection, and the same login gets the same answer. A login that never
                    // worked is held: only Restart (a new address or login) or TryAgain (a person's "Check again") ends
                    // this wait, by cancelling the attempt. One that worked is tried every AskedToWaitPause for a while.
                    var wait = refused switch
                    {
                        ConnectionProblemKind.LoginRefused => RefusedLoginWait(),
                        ConnectionProblemKind.AskedToWait => AskedToWaitPause,
                        _ => backoff,
                    };
                    if (wait == Timeout.InfiniteTimeSpan)
                    {
                        _board.OnStep(BoardPollStep.Held, null);
                    }
                    else
                    {
                        _board.OnStep(BoardPollStep.Waiting, ((int)wait.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }

                    try
                    {
                        // No timer for the hold: only a cancel ends it.
                        await (wait == Timeout.InfiniteTimeSpan
                            ? Task.Delay(Timeout.InfiniteTimeSpan, attempt.Token)
                            : Task.Delay(wait, _timeProvider, attempt.Token)).ConfigureAwait(false);
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

                    if (refused is not (ConnectionProblemKind.LoginRefused or ConnectionProblemKind.AskedToWait))
                    {
                        backoff = backoff * 2 > MaxBackoff ? MaxBackoff : backoff * 2;
                    }
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

    /// <summary>
    /// One connection, from connect until it is lost or ended. Returns whether the board answered a poll, and the kind of
    /// problem a refused connect put on the state (null when it put none, e.g. a Restart came first).
    /// </summary>
    private async Task<(bool Answered, ConnectionProblemKind? Refused)> RunSessionAsync(CancellationToken cancellationToken)
    {
        var answered = false;
        ConnectionProblemKind? refused = null;
        var lost = false;
        var connected = false;
        int generation;
        int restarts;
        lock (_stateLock)
        {
            generation = _generation;
            restarts = _restarts;
        }

        try
        {
            _board.OnStep(BoardPollStep.Connecting, null);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _board.ConnectAsync(cancellationToken).ConfigureAwait(false);
                connected = true;
                _failures = 0;
                _lost = false;
            }
            finally
            {
                _gate.Release();
            }

            // A PollNow from before the connection is no reason to poll twice.
            Interlocked.Exchange(ref _wake, NewWake());
            while (true)
            {
                // Set before taking the gate, so a command that comes now always finds the poll to give up.
                using var poll = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Volatile.Write(ref _poll, poll);
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_lost)
                    {
                        // Commands that failed lost the board; that was reported then.
                        lost = true;
                        return (answered, null);
                    }

                    var reading = await _board.ReadAsync(poll.Token).ConfigureAwait(false);
                    _failures = 0;
                    answered = true;
                    LoginWorked(restarts);
                    Apply(reading, generation);
                }
                catch (OperationCanceledException) when (poll.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // A command took the poll's place: no failure, and the command brings the state.
                }
#pragma warning disable CA1031 // Whatever a poll throws counts as a failed poll; the loop must go on.
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
                {
                    if (CountFailure(ex.Message))
                    {
                        lost = true;
                        return (answered, null);
                    }
                }
                finally
                {
                    Volatile.Write(ref _poll, null);
                    _gate.Release();
                }

                await WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // A failed connect is logged by the board and tried again; the loop must go on.
        catch (Exception ex) when (!lost)
#pragma warning restore CA1031
        {
            // Whatever ended it once the app restarted or stopped it, the app did.
            if (cancellationToken.IsCancellationRequested)
            {
                _board.OnStep(BoardPollStep.Stopped, null);
            }
            else
            {
                _board.OnStep(BoardPollStep.Lost, ex.Message);

                // Why the board refused, or null when it didn't say (e.g. no answer at all). A Forget or Restart since
                // the connect began, also one that came just now, drops it (SetProblem).
                if (!connected)
                {
                    refused = SetProblem((ex as IConnectionProblemSource)?.Problem, generation, restarts);
                }
            }
        }
        finally
        {
            if (!lost)
            {
                // A command that is under way finishes first; it takes at most its own timeout.
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    SetDisconnected();
                }
                finally
                {
                    _gate.Release();
                }
            }
        }

        return (answered, refused);
    }

    // After a refused login: held (infinite) for a login that never connected since the last Restart, or once its
    // RefusedLoginRetries from the first refusal are over. Otherwise the next try after AskedToWaitPause.
    private TimeSpan RefusedLoginWait()
    {
        lock (_stateLock)
        {
            return _refusedLogin.WaitAfterRefusal(_timeProvider.GetUtcNow());
        }
    }

    // A poll answered: the login works, and the time of its refusals starts over. Not for an answer to the login before
    // a Restart that is still on its way.
    private void LoginWorked(int restarts)
    {
        lock (_stateLock)
        {
            if (restarts == _restarts)
            {
                _refusedLogin.Worked();
            }
        }
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

    // Callers hold _gate. A reading from a board Forget has moved on from is dropped.
    private void Apply(TReading reading, int generation)
    {
        lock (_stateLock)
        {
            if (generation != _generation)
            {
                return;
            }

            var before = State;
            var after = new PolledState<TReading>(true, reading, _timeProvider.GetUtcNow());
            Volatile.Write(ref _state, after);
            if (!before.IsConnected || before.Reading is null || !_comparer.Equals(before.Reading, reading))
            {
                _board.OnStateChanged(after);
            }
            else if (!EqualityComparer<TReading>.Default.Equals(before.Reading, reading))
            {
                // A change the comparer leaves out, e.g. an analog value alone: told apart, so what listens only to
                // OnStateChanged (the safety services) isn't woken by it.
                _board.OnReadingChanged(after);
            }
        }
    }

    // A problem from a connect for a board Forget has moved on from, or a login Restart has moved on from, is dropped.
    // The same problem again is no change to report. Returns the kind of the problem now on the state for this connect,
    // or null when it was dropped or there is none.
    private ConnectionProblemKind? SetProblem(ConnectionProblem? problem, int generation, int restarts)
    {
        lock (_stateLock)
        {
            var state = _state;
            if (generation != _generation || restarts != _restarts)
            {
                return null;
            }

            if (!Equals(state.Problem, problem))
            {
                var after = state with { Problem = problem };
                Volatile.Write(ref _state, after);
                _board.OnStateChanged(after);
            }

            return problem?.Kind;
        }
    }

    // Callers hold _gate. Counts a poll or command that failed; returns whether that lost the board, which is
    // then disconnected before the gate opens again, so no command gets through after it.
    private bool CountFailure(string reason)
    {
        _failures++;
        if (_lost || _failures < FailuresBeforeLost)
        {
            return _lost;
        }

        _lost = true;
        SetDisconnected();
        _board.OnStep(BoardPollStep.Lost, reason);

        // Ends the wait at once, so the poller connects again without polling a board that is gone.
        Volatile.Read(ref _wake).TrySetResult();
        return true;
    }

    // Callers hold _gate.
    private void SetDisconnected()
    {
        lock (_stateLock)
        {
            var state = State;
            if (state.IsConnected)
            {
                var after = state with { IsConnected = false };
                Volatile.Write(ref _state, after);
                _board.OnStateChanged(after);
            }
        }
    }

    private static void Cancel(ref CancellationTokenSource? source)
    {
        try
        {
            Volatile.Read(ref source)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It had just ended.
        }
    }

    private static TaskCompletionSource NewWake()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
