namespace Contactor.Web.Features.Boards;

/// <summary>
/// Keeps every board's connection open for the app's lifetime (<see cref="BoardRegistry.RunAsync"/>), each at the
/// address and login saved on its row, if any. A board without a model waits for one; a board without an address yet
/// (the setup guide asks for it) waits until one is saved, and tries nothing before.
/// </summary>
public sealed class BoardHostedService : BackgroundService
{
    private readonly BoardRegistry _registry;

    public BoardHostedService(BoardRegistry registry)
    {
        _registry = registry;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return _registry.RunAsync(stoppingToken);
    }
}
