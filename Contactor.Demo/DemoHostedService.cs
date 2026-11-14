namespace Contactor.Demo;

/// <summary>
/// The copies' lifetime: clears what copies left in the data root at start, stops idle copies every minute, and stops
/// every copy, processes and data, when the demo stops.
/// </summary>
public sealed class DemoHostedService : BackgroundService
{
    /// <summary>How often idle copies are looked for.</summary>
    public static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);

    private readonly VisitorCopies _copies;
    private readonly TimeProvider _time;

    public DemoHostedService(VisitorCopies copies, TimeProvider time)
    {
        _copies = copies;
        _time = time;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _copies.CleanDataRoot();
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await _copies.StopAllAsync();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(SweepEvery, _time, stoppingToken);
                await _copies.StopIdleAsync();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The demo stops.
        }
    }
}
