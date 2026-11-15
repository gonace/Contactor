using Contactor.Boards;

namespace Contactor.Boards.DAEnetIP2v2;

public sealed class DAEnetIP2v2Traffic : IBoardTraffic, IDisposable
{
    private readonly DAEnetIP2v2Client _client;
    public DAEnetIP2v2Traffic(DAEnetIP2v2Client client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _client.Traced += OnTraced;
    }
    public event EventHandler<BoardTraceEntry>? Traced;
    public void Dispose() => _client.Traced -= OnTraced;
    private void OnTraced(object? sender, BoardTraceEntry entry) => Traced?.Invoke(this, entry);
}
