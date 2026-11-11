using Contactor.Vm201;

namespace Contactor.Web.Features.Vm201;

/// <summary>A step on the board connection as the Debug screen and the toasts show it.</summary>
/// <param name="Id">Increases by one per item, so a page can tell new ones apart.</param>
/// <param name="AtUtc">When it happened.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Text">In Swedish, e.g. "Slå på relä 3".</param>
/// <param name="Hex">The packet's bytes, or null when there is no packet.</param>
/// <param name="IsError">Shown in red.</param>
/// <param name="IsRoutine">
/// No toast: the keep-alive (a status request, or a status the same as the one before) and the nine channel names
/// the board sends on every connection.
/// </param>
public sealed record BoardTraceItem(
    long Id,
    DateTimeOffset AtUtc,
    Vm201TraceKind Kind,
    string Text,
    string? Hex,
    bool IsError,
    bool IsRoutine);

/// <summary>
/// The latest steps on the board connection, in memory only (nothing is stored, and a restart empties it), for the
/// Debug screen. It listens from the moment it is created, which <see cref="Vm201HostedService"/> makes happen before
/// the first connection.
/// </summary>
public sealed class BoardTrace : IDisposable
{
    public const int Capacity = 100;

    private readonly Vm201Client _client;
    private readonly Lock _lock = new();
    private readonly Queue<BoardTraceItem> _items = new(Capacity);
    private long _nextId = 1;
    private byte[]? _lastStatus;

    public BoardTrace(Vm201Client client)
    {
        _client = client;
        _client.Traced += OnTraced;
    }

    /// <summary>Raised with each new item, on the board connection's loop: handlers must be quick.</summary>
    public event EventHandler<BoardTraceItem>? Added;

    /// <summary>Raised when <see cref="Clear"/> empties the list.</summary>
    public event EventHandler? Cleared;

    /// <summary>The items, newest first.</summary>
    public IReadOnlyList<BoardTraceItem> Items
    {
        get
        {
            lock (_lock)
            {
                return [.. _items.Reverse()];
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
        }

        Cleared?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _client.Traced -= OnTraced;
    }

    private void OnTraced(object? sender, Vm201TraceEntry entry)
    {
        Record(entry);
    }

    /// <summary>Adds a step; the client's <see cref="Vm201Client.Traced"/> calls it, and the tests do.</summary>
    public void Record(Vm201TraceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var (text, hex, isError) = BoardTraceText.Describe(entry);
        BoardTraceItem item;
        lock (_lock)
        {
            item = new BoardTraceItem(_nextId++, entry.At, entry.Kind, text, hex, isError, IsRoutine(entry));
            if (_items.Count == Capacity)
            {
                _items.Dequeue();
            }

            _items.Enqueue(item);
        }

        Added?.Invoke(this, item);
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
