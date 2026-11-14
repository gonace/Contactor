using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Inputs;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Events;

/// <summary>
/// Logs what happens at each board without the app asking: relays turned on or off through the board's web page,
/// its button or its timers (Actor <c>external</c>), changes to the inputs, and the connection being lost for more
/// than 30 s and coming back, each event with the id of the board it is about. Each board has its own recorder
/// (<see cref="BoardRecorder"/>), made with its live board (<see cref="IBoardWatcher"/>) and run on its own loop while
/// the app runs, so a board that is lost or slow never holds up another's. An input's change seen as it happened is
/// handed on to its actions (<see cref="InputChangeFeed"/>) once it is logged.
/// </summary>
public sealed class BoardEventRecorder : BackgroundService, IBoardWatcher
{
    public static readonly TimeSpan ConnectionLostAfter = TimeSpan.FromSeconds(30);

    // How often a lasting disconnect is checked for while there is no new status.
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The longest wait before a status whose recording failed is tried again. The wait starts at
    /// <see cref="CheckInterval"/> and doubles with each failure in a row, so a database that stays broken fills the
    /// log with one error a minute, not one a second.
    /// </summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(1);

    /// <summary>A gap in contact longer than this, e.g. a crash or a board that was off, makes the inputs' times unknown.</summary>
    public static readonly TimeSpan LongGap = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How often the time of contact is saved while there is contact: rarely, to spare the SD card. A gap is measured
    /// from it, so it can read up to this much longer than it was.
    /// </summary>
    public static readonly TimeSpan ContactSaveInterval = TimeSpan.FromMinutes(5);

    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly LocalClock _clock;
    private readonly RelayChanges _changes;
    private readonly ILoggerFactory _loggerFactory;
    private readonly InputChangeFeed? _inputChanges;
    private readonly BoardParts<BoardRecorder> _boards;

    public BoardEventRecorder(
        IDbContextFactory<ContactorDbContext> dbFactory,
        LocalClock clock,
        RelayChanges changes,
        ILoggerFactory loggerFactory,
        InputChangeFeed? inputChanges = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _dbFactory = dbFactory;
        _clock = clock;
        _changes = changes;
        _loggerFactory = loggerFactory;
        _inputChanges = inputChanges;
        _boards = new BoardParts<BoardRecorder>(loggerFactory.CreateLogger<BoardEventRecorder>());
    }

    /// <summary>How many boards' loops run now (see <see cref="BoardParts{T}.RunningLoops"/>).</summary>
    public int RunningLoops => _boards.RunningLoops;

    /// <summary>The board's recorder, or null for a board the app doesn't run.</summary>
    public BoardRecorder? For(int boardId)
    {
        return _boards.Find(boardId);
    }

    /// <inheritdoc/>
    public IAsyncDisposable Watch(LiveBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);
        var recorder = new BoardRecorder(board, _dbFactory, _clock, _changes, _loggerFactory.CreateLogger<BoardRecorder>(), _inputChanges);
        _boards.Add(recorder);
        return new Watching(_boards, recorder);
    }

    // Each board's statuses from before the recorder started count as one, as the board stood then; read at once, so
    // a status that comes after this is a change the app saw.
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var recorder in _boards.All)
        {
            recorder.Begin();
        }

        return base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return _boards.RunAsync(stoppingToken);
    }

    // Ends one board's watch: its recorder leaves the list, then stops.
    private sealed class Watching : IAsyncDisposable
    {
        private readonly BoardParts<BoardRecorder> _boards;
        private readonly BoardRecorder _recorder;

        public Watching(BoardParts<BoardRecorder> boards, BoardRecorder recorder)
        {
            _boards = boards;
            _recorder = recorder;
        }

        public ValueTask DisposeAsync()
        {
            _boards.Remove(_recorder);
            return _recorder.DisposeAsync();
        }
    }
}
