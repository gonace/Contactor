using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Advantech;

/// <summary>A small Modbus TCP simulator for the ADAM-6260 relay map.</summary>
public sealed class Adam6200Simulator : ISimulatedBoard
{
    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly List<TcpClient> _clients = [];
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private ulong _relays;

    public Adam6200Simulator(BoardModel model, ILogger logger, TimeProvider time)
    {
        if (!Adam6200Models.All.Contains(model))
        {
            throw new ArgumentException("The model is not an ADAM-6200 board.", nameof(model));
        }
        Model = model;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public BoardModel Model { get; }
    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;
    public bool HasLogin => false;
    public event EventHandler? Changed;
    public event EventHandler<BoardTraceEntry>? Traced;
    public SimulatedBoardState State
    {
        get
        {
            lock (_gate)
            {
                return new(new(_relays), new(0), _clients.Select(c => c.Client.RemoteEndPoint?.ToString() ?? "client").ToArray());
            }
        }
    }

    public void Start(int port = 0)
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("The simulator has already started.");
        }
        _stop = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _ = AcceptAsync(_stop.Token);
    }

    public Task SwitchRelayAsync(int relay, bool turnOn)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(relay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relay, 6);
        lock (_gate)
        {
            var bit = 1UL << (relay - 1);
            _relays = turnOn ? _relays | bit : _relays & ~bit;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task SetInputAsync(int input, bool closed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(input, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input, Model.Capabilities.InputCount);
        return Task.CompletedTask;
    }

    public void DropConnections()
    {
        TcpClient[] clients;
        lock (_gate)
        {
            clients = [.. _clients];
        }
        foreach (var client in clients)
        {
            client.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        _stop?.Cancel();
        _listener?.Stop();
        DropConnections();
        _stop?.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task AcceptAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (token.IsCancellationRequested) { break; }
            lock (_gate)
            {
                _clients.Add(client);
            }
            Changed?.Invoke(this, EventArgs.Empty);
            _ = ServeAsync(client, token);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            var stream = client.GetStream();
            while (!token.IsCancellationRequested)
            {
                var header = new byte[ModbusTcpFrame.HeaderLength];
                try { await stream.ReadExactlyAsync(header, token).ConfigureAwait(false); }
                catch (EndOfStreamException) { break; }
                var total = ModbusTcpFrame.GetFrameLength(header);
                var request = new byte[total];
                header.CopyTo(request, 0);
                await stream.ReadExactlyAsync(request.AsMemory(header.Length), token).ConfigureAwait(false);
                var pdu = request.AsSpan(ModbusTcpFrame.HeaderLength).ToArray();
                Trace(BoardTraceDirection.Sent, "Modbus request", Hex(request));
                var answerPdu = Handle(pdu);
                var answer = ModbusTcpFrame.Encode(BinaryPrimitives.ReadUInt16BigEndian(request), request[6], answerPdu);
                await stream.WriteAsync(answer, token).ConfigureAwait(false);
                Trace(BoardTraceDirection.Received, "Modbus response", Hex(answer));
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidDataException or ObjectDisposedException)
        {
            if (!token.IsCancellationRequested)
            {
                Adam6200SimulatorLog.ConnectionEnded(_logger, ex);
            }
        }
        finally
        {
            lock (_gate)
            {
                _clients.Remove(client);
            }
            client.Dispose();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private byte[] Handle(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length != 5)
        {
            return [pdu.IsEmpty ? (byte)0x81 : (byte)(pdu[0] | 0x80), 0x03];
        }

        var function = pdu[0];
        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        var countOrValue = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        byte[]? answer = null;
        var changed = false;
        byte exception = 0x01;
        lock (_gate)
        {
            if (function == Adam6200Modbus.ReadCoils)
            {
                exception = address == Adam6200Modbus.RelayCoilBase
                    ? countOrValue == Adam6200Modbus.RelayCount ? (byte)0 : (byte)0x03
                    : (byte)0x02;
                if (exception == 0)
                {
                    return [function, 0x01, (byte)_relays];
                }
            }
            if (function == Adam6200Modbus.WriteSingleCoil)
            {
                if (address < Adam6200Modbus.RelayCoilBase || address >= Adam6200Modbus.RelayCoilBase + Adam6200Modbus.RelayCount)
                {
                    exception = 0x02;
                }
                else if (countOrValue is not (0x0000 or 0xFF00))
                {
                    exception = 0x03;
                }
                else
                {
                    var bit = 1UL << (address - Adam6200Modbus.RelayCoilBase);
                    _relays = countOrValue == 0xFF00 ? _relays | bit : _relays & ~bit;
                    answer = pdu.ToArray();
                    changed = true;
                }
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return answer!;
        }
        return [(byte)(function | 0x80), exception];
    }

    private void Trace(BoardTraceDirection direction, string text, string bytes) =>
        Traced?.Invoke(this, new BoardTraceEntry(_time.GetUtcNow(), direction, text, bytes, false, false));

    private static string Hex(IEnumerable<byte> bytes) => string.Join(' ', bytes.Select(value => value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
}

internal static partial class Adam6200SimulatorLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "A simulated ADAM-6200 connection ended.")]
    internal static partial void ConnectionEnded(ILogger logger, Exception exception);
}
