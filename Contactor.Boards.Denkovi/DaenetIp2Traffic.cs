namespace Contactor.Boards.Denkovi;

/// <summary>
/// The DAEnetIP2 v2's traffic as an <see cref="IBoardTraffic"/>: each request, answer and step
/// <see cref="DaenetIp2Client.Traced"/> reports, already in plain words (<see cref="DaenetIp2TraceText"/>), with the
/// PDU's bytes and never the community. The polls and the answers that change nothing are routine.
/// </summary>
public sealed class DaenetIp2Traffic : IBoardTraffic, IDisposable
{
    private readonly DaenetIp2Client _client;

    public DaenetIp2Traffic(DaenetIp2Client client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _client.Traced += OnTraced;
    }

    public event EventHandler<BoardTraceEntry>? Traced;

    public void Dispose()
    {
        _client.Traced -= OnTraced;
    }

    private void OnTraced(object? sender, BoardTraceEntry entry)
    {
        Traced?.Invoke(this, entry);
    }
}
