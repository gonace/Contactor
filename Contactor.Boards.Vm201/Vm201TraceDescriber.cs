namespace Contactor.Boards.Vm201;

/// <summary>
/// Turns VM201 trace steps into <see cref="BoardTraceEntry"/>s in plain words (<see cref="Vm201TraceText"/>), for the
/// app's Debug screen and the simulator alike. The keep-alive (a status request, or a status the same as the one
/// before) and the nine channel names the board sends on every connection count as routine, so it remembers the last
/// status: use one per connection log. Thread-safe.
/// </summary>
public sealed class Vm201TraceDescriber
{
    private readonly Lock _lock = new();
    private byte[]? _lastStatus;

    public BoardTraceEntry Describe(Vm201TraceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var (text, hex, isError) = Vm201TraceText.Describe(entry);
        var direction = entry.Kind switch
        {
            Vm201TraceKind.Sent => BoardTraceDirection.Sent,
            Vm201TraceKind.Received => BoardTraceDirection.Received,
            _ => BoardTraceDirection.None,
        };
        bool routine;
        lock (_lock)
        {
            routine = IsRoutine(entry);
        }

        return new BoardTraceEntry(entry.At, direction, text, hex, isError, routine);
    }

    // Called under the lock: remembers the last status, so an unchanged one counts as the keep-alive.
    private bool IsRoutine(Vm201TraceEntry entry)
    {
        switch (entry.Kind)
        {
            case Vm201TraceKind.Sent when entry.Command == Vm201Command.StatusRequest:
            case Vm201TraceKind.Received when entry.Command == Vm201Command.Name:
                return true;
            case Vm201TraceKind.Received when entry.Command == Vm201Command.Status:
                var status = entry.Data.ToArray();
                var same = _lastStatus is not null && _lastStatus.AsSpan().SequenceEqual(status);
                _lastStatus = status;
                return same;
            case Vm201TraceKind.Connecting or Vm201TraceKind.ConnectionLost:
                // The first status on a new connection always gets a toast.
                _lastStatus = null;
                return false;
            default:
                return false;
        }
    }
}
