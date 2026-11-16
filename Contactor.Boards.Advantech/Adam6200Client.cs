using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Contactor.Boards.Advantech;

/// <summary>Polling Modbus TCP client for the ADAM-6260 relay map.</summary>
public sealed class Adam6200Client : IPolledBoard<Adam6200Reading>, IDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private readonly Adam6200Options _options;
    private readonly TimeProvider _time;
    private readonly ILogger<Adam6200Client> _logger;
    private readonly BoardPoller<Adam6200Reading> _poller;
    private BoardEndpoint _endpoint;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private int _transactionId;
    private int _disposed;

    public Adam6200Client(IOptions<Adam6200Options> options, BoardModel model, TimeProvider time, ILogger<Adam6200Client> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        if (!Adam6200Models.All.Contains(model))
        {
            throw new ArgumentException("The model is not an ADAM-6200 relay board.", nameof(model));
        }

        _options = options.Value;
        if (_options.PollSeconds < 1)
        {
            throw new ArgumentException("AdvantechAdam6200:PollSeconds must be at least 1.", nameof(options));
        }
        if (_options.Port is < 1 or > 65535)
        {
            throw new ArgumentException("AdvantechAdam6200:Port must be a TCP port from 1 to 65535.", nameof(options));
        }

        _endpoint = new BoardEndpoint(_options.Host, _options.Port);
        Model = model;
        _time = time;
        _logger = logger;
        _poller = new BoardPoller<Adam6200Reading>(this, time) { PollInterval = TimeSpan.FromSeconds(_options.PollSeconds) };
    }

    public BoardModel Model { get; }
    public event EventHandler<Adam6200State>? StateChanged;
    public event EventHandler<BoardTraceEntry>? Traced;
    public Adam6200State State
    {
        get
        {
            var state = _poller.State;
            return new(state.IsConnected, state.Reading, state.LastContactUtc);
        }
    }
    public BoardEndpoint Endpoint => Volatile.Read(ref _endpoint);

    public void SetEndpoint(BoardEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (Interlocked.Exchange(ref _endpoint, endpoint) == endpoint)
        {
            return;
        }
        _poller.Forget();
        _poller.Restart();
    }

    public Task RunAsync(CancellationToken cancellationToken) => _poller.RunAsync(cancellationToken);
    public Task ConnectAsync(CancellationToken cancellationToken) => OpenAsync(cancellationToken);
    public Task<Adam6200Reading> ReadAsync(CancellationToken cancellationToken) => ReadStateAsync(cancellationToken);

    public Task<ulong> SetAsync(ulong mask, bool on, CancellationToken cancellationToken = default)
    {
        if ((mask & ~Model.Capabilities.AllRelays.Bits) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mask), mask, "The ADAM-6260 has relays 1–6 only.");
        }

        if (mask == 0)
        {
            return Task.FromResult(0UL);
        }
        var accepted = 0UL;
        return SetCoreAsync();

        async Task<ulong> SetCoreAsync()
        {
            await _poller.RunCommandAsync(async token =>
            {
                string? refusal = null;
                string? failure = null;
                for (var channel = 1; channel <= Adam6200Modbus.RelayCount; channel++)
                {
                    var bit = 1UL << (channel - 1);
                    if ((mask & bit) == 0)
                    {
                        continue;
                    }

                    var pdu = Adam6200Modbus.WriteRelay(channel, on);
                    try
                    {
                        var response = await ExchangeAsync(pdu, routine: false, token).ConfigureAwait(false);
                        if (!response.AsSpan(ModbusTcpFrame.HeaderLength).SequenceEqual(pdu))
                        {
                            throw new InvalidDataException("The ADAM-6200 did not echo the relay command.");
                        }
                        accepted |= bit;
                    }
                    catch (ModbusExceptionResponseException ex) when (ex.Function == Adam6200Modbus.WriteSingleCoil)
                    {
                        refusal ??= ex.Message;
                        Trace(BoardTraceDirection.None, $"The module refused relay {channel}: {ex.Message}", isError: true);
                    }
                    catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException)
                    {
                        failure ??= ex.Message;
                        Trace(BoardTraceDirection.None, $"The relay {channel} command was not confirmed: {ex.Message}", isError: true);
                    }
                }

                if (accepted == 0)
                {
                    if (failure is not null)
                    {
                        throw new BoardNotConnectedException($"No ADAM-6260 relay command was confirmed; a relay may have changed state. {failure}");
                    }
                    throw new BoardRefusedException(refusal ?? "The module did not accept any relay command.");
                }

                try
                {
                    return await ReadStateAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or ModbusExceptionResponseException)
                {
                    if (_poller.State.Reading is not { } last)
                    {
                        throw;
                    }

                    Trace(BoardTraceDirection.None, $"The relay command was answered, but its state could not be read: {ex.Message}", isError: true);
                    _poller.PollNow();
                    return last;
                }
            }, cancellationToken).ConfigureAwait(false);
            return accepted;
        }
    }

    public void Refresh()
    {
        if (!State.IsConnected)
        {
            throw new BoardNotConnectedException();
        }
        _poller.PollNow();
    }

    public void OnStateChanged(PolledState<Adam6200Reading> state) => Raise(StateChanged, new(state.IsConnected, state.Reading, state.LastContactUtc));
    public void OnStep(BoardPollStep kind, string? detail) => Raise(Traced, new BoardTraceEntry(_time.GetUtcNow(), BoardTraceDirection.None, detail ?? kind.ToString(), null, false, false));
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) { _poller.Dispose(); Close(); } }

    private async Task<Adam6200Reading> ReadStateAsync(CancellationToken token)
    {
        var pdu = Adam6200Modbus.ReadRelays();
        var answer = await ExchangeAsync(pdu, routine: true, token).ConfigureAwait(false);
        return new(Adam6200Modbus.ReadRelayBits(answer.AsSpan(ModbusTcpFrame.HeaderLength)));
    }

    private async Task<byte[]> ExchangeAsync(byte[] pdu, bool routine, CancellationToken token)
    {
        if (_stream is null)
        {
            await OpenAsync(token).ConfigureAwait(false);
        }
        var id = unchecked((ushort)Interlocked.Increment(ref _transactionId));
        var request = ModbusTcpFrame.Encode(id, _options.UnitId, pdu);
        Trace(BoardTraceDirection.Sent, $"Modbus function {pdu[0]:X2}", Hex(request));
        using var timeout = new CancellationTokenSource(RequestTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try
        {
            await _stream!.WriteAsync(request, linked.Token).ConfigureAwait(false);
            var header = new byte[ModbusTcpFrame.HeaderLength];
            await _stream.ReadExactlyAsync(header, linked.Token).ConfigureAwait(false);
            var length = ModbusTcpFrame.GetFrameLength(header);
            var answer = new byte[length];
            header.CopyTo(answer, 0);
            await _stream.ReadExactlyAsync(answer.AsMemory(header.Length), linked.Token).ConfigureAwait(false);
            ModbusTcpFrame.ValidateAnswer(answer, id, _options.UnitId, pdu[0]);
            Trace(BoardTraceDirection.Received, "Modbus answer", Hex(answer), isRoutine: routine);
            return answer;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
        {
            Close();
            throw new TimeoutException($"The ADAM-6200 did not answer within {RequestTimeout.TotalSeconds} seconds.");
        }
        catch (ModbusExceptionResponseException)
        {
            throw;
        }
        catch { Close(); throw; }
    }

    private async Task OpenAsync(CancellationToken token)
    {
        Close();
        var client = new TcpClient { NoDelay = true };
        using var timeout = new CancellationTokenSource(RequestTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try
        {
            var endpoint = Endpoint;
            await client.ConnectAsync(endpoint.Host, endpoint.Port, linked.Token).ConfigureAwait(false);
            _tcp = client;
            _stream = client.GetStream();
        }
        catch { client.Dispose(); throw; }
    }

    private void Close()
    {
        var old = Interlocked.Exchange(ref _tcp, null);
        _stream = null;
        old?.Dispose();
    }

    private void Trace(BoardTraceDirection direction, string text, string? bytes = null, bool isError = false, bool isRoutine = false) =>
        Raise(Traced, new BoardTraceEntry(_time.GetUtcNow(), direction, text, bytes, isError, isRoutine));

    private static string Hex(IEnumerable<byte> bytes) => string.Join(' ', bytes.Select(value => value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

    private void Raise<T>(EventHandler<T>? handlers, T value)
    {
        if (handlers is null)
        {
            return;
        }
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<T>>())
        {
            try { handler(this, value); }
#pragma warning disable CA1031
            catch (Exception ex) { Adam6200Log.SubscriberFailed(_logger, ex); }
#pragma warning restore CA1031
        }
    }
}

internal static partial class Adam6200Log
{
    [LoggerMessage(Level = LogLevel.Error, Message = "An ADAM-6200 subscriber failed.")]
    internal static partial void SubscriberFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "An ADAM-6200 state subscriber failed.")]
    internal static partial void StateSubscriberFailed(ILogger logger, Exception exception);
}
