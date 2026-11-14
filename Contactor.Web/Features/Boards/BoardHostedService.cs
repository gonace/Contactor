namespace Contactor.Web.Features.Boards;

/// <summary>
/// Keeps every board's connection open for the app's lifetime (<see cref="BoardRegistry.RunAsync"/>), each at the
/// address and login saved on its row, if any. A board without a model waits for one; a board without an address yet
/// (the setup guide asks for it) waits until one is saved, and tries nothing before. In a host, the connections start
/// only once every hosted service has started (<see cref="IHostApplicationLifetime.ApplicationStarted"/>): what watches
/// the boards (the group sequencer, the event recorder, the scheduler, auto-off) runs before the first status, which
/// each sees in order.
/// </summary>
public sealed class BoardHostedService : BackgroundService
{
    private readonly BoardRegistry _registry;
    private readonly IHostApplicationLifetime? _lifetime;

    public BoardHostedService(BoardRegistry registry, IHostApplicationLifetime? lifetime = null)
    {
        _registry = registry;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_lifetime is not null)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (_lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
            {
                try
                {
                    await started.Task.WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }

        await _registry.RunAsync(stoppingToken);
    }
}
