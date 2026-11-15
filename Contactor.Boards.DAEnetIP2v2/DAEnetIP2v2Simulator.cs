using System.Globalization;
using System.Net;
using Contactor.Boards;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Boards.DAEnetIP2v2;

/// <summary>
/// Local HTTP simulator for both DAEnetIP2v2 relay-kit variants. It implements the legacy IO read/write routes used
/// by the client and listens only on loopback.
/// </summary>
public sealed partial class DAEnetIP2v2Simulator : ISimulatedBoard
{
    private readonly Lock _lock = new();
    private readonly ILogger _logger;
    private readonly string? _username;
    private readonly string? _password;
    private readonly CancellationTokenSource _stopping = new();
    private HttpListener? _listener;
    private Task? _acceptLoop;
    private ulong _relays;

    public DAEnetIP2v2Simulator(BoardModel model) : this(model, null, null, NullLogger.Instance) { }
    public DAEnetIP2v2Simulator(BoardModel model, ILogger logger) : this(model, null, null, logger) { }
    public DAEnetIP2v2Simulator(BoardModel model, string? username, string? password, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(logger);
        if (!DAEnetIP2v2Models.IsSupported(model)) throw new ArgumentException("Not a DAEnetIP2v2 model.", nameof(model));
        Model = model;
        _username = string.IsNullOrEmpty(username) ? null : username;
        _password = string.IsNullOrEmpty(password) ? null : password;
        _logger = logger;
    }

    public BoardModel Model { get; }
    public int Port { get; private set; }
    public bool HasLogin => _username is not null || _password is not null;
    public event EventHandler? Changed;
    public event EventHandler<BoardTraceEntry>? Traced;
    public SimulatedBoardState State
    {
        get { lock (_lock) return new(new ChannelMask(_relays), ChannelMask.None, []); }
    }
    public ulong Relays { get { lock (_lock) return _relays; } }

    public void Start(int port = 0)
    {
        if (_listener is not null) throw new InvalidOperationException("The simulator is already running.");
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        listener.Start();
        _listener = listener;
        _acceptLoop = AcceptAsync(listener, _stopping.Token);
    }

    private async Task AcceptAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested) { break; }
            _ = HandleAsync(context, cancellationToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var request = context.Request;
            Trace(BoardTraceDirection.Sent, request.Url?.PathAndQuery ?? "HTTP request", false);
            if (HasLogin)
            {
                var expected = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{_username}:{_password}"));
                if (!string.Equals(request.Headers["Authorization"], expected, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 401;
                    context.Response.AddHeader("WWW-Authenticate", "Basic realm=\"DAEnetIP2v2\"");
                    context.Response.Close();
                    return;
                }
            }
            if (request.Url?.AbsolutePath.Equals("/ioreg.js", StringComparison.OrdinalIgnoreCase) == true)
            {
                ulong relays;
                lock (_lock) relays = _relays;
                var p5 = relays & 0xff;
                var p3 = (relays >> 8) & 0xff;
                var body = $"var IO = [{p3.ToString(CultureInfo.InvariantCulture)},{p5.ToString(CultureInfo.InvariantCulture)},0,0,0,0,0,0,0,0,0];";
                context.Response.ContentType = "application/javascript";
                await WriteAsync(context, body, cancellationToken).ConfigureAwait(false);
                Trace(BoardTraceDirection.Received, "Returned relay state", false);
                return;
            }

            if (request.Url?.AbsolutePath.Equals("/iochange.cgi", StringComparison.OrdinalIgnoreCase) == true)
            {
                lock (_lock)
                {
                    foreach (var key in request.QueryString.AllKeys.Where(key => key is not null))
                    {
                        if (key is null || key.Length != 3 || key[0] != 'P' || key[1] is not ('3' or '5') ||
                            !int.TryParse(key[2..], NumberStyles.None, CultureInfo.InvariantCulture, out var channel) ||
                            channel is < 1 or > 8 ||
                            !int.TryParse(request.QueryString[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
                            value is not (0 or 1))
                            continue;

                        var relay = key[1] == '5' ? channel : channel + 8;
                        var bit = 1UL << (relay - 1);
                        _relays = value == 1 ? _relays | bit : _relays & ~bit;
                    }
                    _relays &= Model.Capabilities.AllRelays.Bits;
                }
                context.Response.ContentType = "text/plain";
                await WriteAsync(context, "OK", cancellationToken).ConfigureAwait(false);
                Trace(BoardTraceDirection.Received, "Accepted relay output update", false);
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }

            context.Response.StatusCode = 404;
            await WriteAsync(context, "Not found", cancellationToken).ConfigureAwait(false);
        }
        #pragma warning disable CA1031 // A failed HTTP request must not stop the simulator.
        catch (Exception ex) when (ex is not OperationCanceledException)
        #pragma warning restore CA1031
        {
            LogRequestFailed(ex);
            try { context.Response.StatusCode = 500; context.Response.Close(); } catch (HttpListenerException) { }
        }
    }

    private static async Task WriteAsync(HttpListenerContext context, string body, CancellationToken cancellationToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        context.Response.Close();
    }

    public Task SwitchRelayAsync(int relay, bool turnOn)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(relay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relay, Model.Capabilities.RelayCount);
        lock (_lock) _relays = turnOn ? _relays | (1UL << (relay - 1)) : _relays & ~(1UL << (relay - 1));
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task SetInputAsync(int input, bool closed) =>
        throw new ArgumentOutOfRangeException(nameof(input), "The relay kits have no supported digital inputs.");

    public void DropConnections()
    {
        // HTTP is request-oriented; closing the listener makes current and future requests fail.
        _listener?.Close();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The DAEnetIP2v2 simulator request failed.")]
    private partial void LogRequestFailed(Exception exception);

    private void Trace(BoardTraceDirection direction, string message, bool error) =>
        Traced?.Invoke(this, new BoardTraceEntry(DateTimeOffset.UtcNow, direction, message, null, error, false));

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener?.Close();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (HttpListenerException) { }
        }
        _listener = null;
        _stopping.Dispose();
    }
}
