namespace Contactor.Boards.Vm208;

/// <summary>
/// The VM208's traffic as an <see cref="IBoardTraffic"/>: each request, answer and connection step
/// <see cref="Vm208Client.Traced"/> reports, already in plain words (<see cref="Vm208TraceText"/>). The polls and the
/// answers that change nothing are routine, like the VM201's keep-alive.
/// </summary>
public sealed class Vm208Traffic : IBoardTraffic, IDisposable
{
    private readonly Vm208Client _client;

    public Vm208Traffic(Vm208Client client)
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
