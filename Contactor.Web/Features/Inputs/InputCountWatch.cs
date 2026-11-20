using System.Collections.Concurrent;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Relays;

namespace Contactor.Web.Features.Inputs;

/// <summary>
/// Tells the screens to load their inputs again (<see cref="RelayChanges"/>) when a board says it has another number of
/// inputs than before (<see cref="Contactor.Boards.BoardCapabilities.InputCountFromBoard"/>, e.g. a Shelly at its
/// first answer): they load the inputs a board has once, and only a state is no reason to load them again. It only
/// tells the screens; the inputs' changes and actions go by the board's state as for every board.
/// </summary>
public sealed class InputCountWatch : IHostedService
{
    private readonly BoardRegistry _registry;
    private readonly RelayChanges _changes;
    private readonly ConcurrentDictionary<int, int> _counts = new();

    public InputCountWatch(BoardRegistry registry, RelayChanges changes)
    {
        _registry = registry;
        _changes = changes;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _registry.StateChanged += OnStateChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _registry.StateChanged -= OnStateChanged;
        return Task.CompletedTask;
    }

    // On a board's connection loop: quick. Before its first state a board has no inputs, as a page that loaded then
    // showed it.
    private void OnStateChanged(object? sender, BoardStateChange change)
    {
        if (_registry.Find(change.BoardId) is not { Capabilities: { InputCountFromBoard: true } capabilities })
        {
            return;
        }

        if (_counts.GetValueOrDefault(change.BoardId) == capabilities.InputCount)
        {
            return;
        }

        _counts[change.BoardId] = capabilities.InputCount;
        _changes.Notify();
    }
}
