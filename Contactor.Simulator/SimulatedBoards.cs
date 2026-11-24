using System.Globalization;
using System.Net.Sockets;
using Contactor.Boards;
using Microsoft.Extensions.Options;

namespace Contactor.Simulator;

/// <summary>
/// The boards running in the simulator: the ones from the settings, and the ones added on the page, which last until
/// the simulator stops. Each listens on its own port on 127.0.0.1.
/// </summary>
public sealed partial class SimulatedBoards : IAsyncDisposable
{
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<SimulatedBoards> _logger;
    private readonly TimeProvider _time;
    private readonly int? _maxBoards;
    private readonly PortRange? _ports;
    private readonly Lock _lock = new();
    private readonly List<RunningBoard> _boards = [];
    private int _nextId = 1;
    private bool _stopped;

    public SimulatedBoards(ILoggerFactory loggers, TimeProvider time, IOptions<SimulatorOptions> options)
    {
        ArgumentNullException.ThrowIfNull(loggers);
        ArgumentNullException.ThrowIfNull(options);
        _loggers = loggers;
        _logger = loggers.CreateLogger<SimulatedBoards>();
        _time = time;
        _maxBoards = options.Value.MaxBoards;
        // Validated at start; should it be read without that, a range that can't be read allows no port.
        var ports = options.Value.Ports ?? "";
        _ports = ports.Length == 0 ? null : PortRange.TryParse(ports, out var range) ? range : new PortRange(0, -1);
    }

    /// <summary>The only ports a board may listen on (<see cref="SimulatorOptions.Ports"/>), or null for any.</summary>
    public PortRange? Ports => _ports;

    /// <summary>Raised when a board is added or removed, or any board's state or traffic changes; from any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>The running boards, by port.</summary>
    public IReadOnlyList<RunningBoard> Boards
    {
        get
        {
            lock (_lock)
            {
                return [.. _boards.OrderBy(b => b.Board.Port)];
            }
        }
    }

    /// <summary>
    /// Starts a board as the page asks, with a login, or none when the user name and password are both empty. Either
    /// may hold any character the model takes, ':' included. Returns null when it runs, otherwise why it can't, in
    /// words for the page.
    /// </summary>
    public string? Add(string? type, int port, string? username, string? password)
    {
        // A model whose login is a password alone takes a password without a user name.
        var passwordOnly = SimulatorCatalogue.Find(type)?.Model.PasswordOnly == true;
        return string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password) && !passwordOnly
            ? "Write the user name too, or leave both empty."
            : Add(type, port, username, password, fromSettings: false);
    }

    /// <summary>
    /// Starts a board of the model <paramref name="type"/> on <paramref name="port"/>, with a login, or none when the
    /// user name and password are both empty or null. Returns null when it runs, otherwise why it can't.
    /// </summary>
    public string? Add(string? type, int port, string? username, string? password, bool fromSettings)
    {
        if (SimulatorCatalogue.Find(type) is not { } model)
        {
            return "Choose a model.";
        }

        if (port is < 1 or > 65535)
        {
            return "The port is a number from 1 to 65535.";
        }

        if (_ports is { } only && !only.Contains(port))
        {
            return string.Create(CultureInfo.InvariantCulture, $"Choose a port from {only.First} to {only.Last}.");
        }

        if (string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password))
        {
            (username, password) = (null, null);
        }
        else
        {
            (username, password) = (username ?? "", password ?? "");
            if (model.CheckLogin(username, password) is { } loginError)
            {
                return loginError;
            }
        }

        lock (_lock)
        {
            if (_stopped)
            {
                return "The simulator is stopping.";
            }

            // The settings' boards always start; only the page is held to the limit.
            if (!fromSettings && _maxBoards is { } max && _boards.Count >= max)
            {
                return max == 1
                    ? "You can have at most 1 board."
                    : string.Create(CultureInfo.InvariantCulture, $"You can have at most {max} boards.");
            }

            if (_boards.Any(b => b.Board.Port == port))
            {
                return string.Create(CultureInfo.InvariantCulture, $"Port {port} is already used by another board here.");
            }

            var board = model.Create(username, password, _loggers.CreateLogger(model.Model.Id), _time);
            try
            {
                board.Start(port);
            }
            catch (SocketException ex)
            {
                board.DisposeAsync().AsTask().GetAwaiter().GetResult();
                return ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? string.Create(CultureInfo.InvariantCulture, $"Port {port} is already in use on this computer.")
                    : string.Create(CultureInfo.InvariantCulture, $"Can't listen on port {port}: {ex.Message}");
            }

            var running = new RunningBoard(_nextId++, board, username, fromSettings, _time);
            running.Changed += OnBoardChanged;
            _boards.Add(running);
            LogStarted(model.Model.Name, port);
        }

        RaiseChanged();
        return null;
    }

    /// <summary>Stops a board and forgets it. Nothing happens for one that isn't running.</summary>
    public async Task RemoveAsync(int id)
    {
        RunningBoard? board;
        lock (_lock)
        {
            board = _boards.FirstOrDefault(b => b.Id == id);
            if (board is null)
            {
                return;
            }

            _boards.Remove(board);
        }

        board.Changed -= OnBoardChanged;
        await board.DisposeAsync().ConfigureAwait(false);
        RaiseChanged();
    }

    public async ValueTask DisposeAsync()
    {
        List<RunningBoard> boards;
        lock (_lock)
        {
            _stopped = true;
            boards = [.. _boards];
            _boards.Clear();
        }

        foreach (var board in boards)
        {
            board.Changed -= OnBoardChanged;
            await board.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnBoardChanged(object? sender, EventArgs e)
    {
        RaiseChanged();
    }

    // Each handler on its own: a page that fails must not stop a board or keep the change from the other pages.
    private void RaiseChanged()
    {
        if (Changed is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
#pragma warning disable CA1031 // A page's bug must not take down the simulator.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogSubscriberFailed(ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Simulating a {Model} on port {Port}")]
    private partial void LogStarted(string model, int port);

    [LoggerMessage(Level = LogLevel.Error, Message = "A page listening to the simulator failed")]
    private partial void LogSubscriberFailed(Exception exception);
}
