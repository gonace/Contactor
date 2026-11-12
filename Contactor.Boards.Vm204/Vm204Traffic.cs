namespace Contactor.Boards.Vm204;

/// <summary>
/// The VM204's traffic as an <see cref="IBoardTraffic"/>: each request, answer and connection step
/// <see cref="Vm204Client.Traced"/> reports, already in plain words (<see cref="Vm204TraceText"/>). The polls and the
/// answers that change nothing are routine, like the VM201's keep-alive.
/// </summary>
public sealed class Vm204Traffic : IBoardTraffic, IDisposable
{
    private readonly Vm204Client _client;

    public Vm204Traffic(Vm204Client client)
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
