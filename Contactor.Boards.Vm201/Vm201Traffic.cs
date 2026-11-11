namespace Contactor.Boards.Vm201;

/// <summary>
/// The VM201's traffic as an <see cref="IBoardTraffic"/>: each step <see cref="Vm201Client.Traced"/> reports, in plain
/// words (<see cref="Vm201TraceDescriber"/>).
/// </summary>
public sealed class Vm201Traffic : IBoardTraffic, IDisposable
{
    private readonly Vm201Client _client;
    private readonly Vm201TraceDescriber _describer = new();

    public Vm201Traffic(Vm201Client client)
    {
        _client = client;
        _client.Traced += OnTraced;
    }

    public event EventHandler<BoardTraceEntry>? Traced;

    public void Dispose()
    {
        _client.Traced -= OnTraced;
    }

    private void OnTraced(object? sender, Vm201TraceEntry entry)
    {
        Traced?.Invoke(this, ToEntry(entry));
    }

    /// <summary>A step as the app shows it; the client's <see cref="Vm201Client.Traced"/> calls it, and the tests do.</summary>
    public BoardTraceEntry ToEntry(Vm201TraceEntry entry)
    {
        return _describer.Describe(entry);
    }
}
