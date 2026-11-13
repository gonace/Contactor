namespace Contactor.Web.Features.Access;

/// <summary>
/// Keeps the chosen way in running for the app's lifetime, e.g. the Cloudflare tunnel. Nothing runs before one is
/// chosen; a new choice stops the one before and starts it.
/// </summary>
public sealed class AccessHostedService : BackgroundService
{
    private readonly AccessChoice _choice;
    private readonly IServiceProvider _services;

    public AccessHostedService(AccessChoice choice, IServiceProvider services)
    {
        _choice = choice;
        _services = services;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var changed = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            void OnChanged(object? sender, EventArgs e)
            {
                try
                {
                    changed.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // That run is over already.
                }
            }

            _choice.Changed += OnChanged;
            try
            {
                if (_choice.Current is { } method)
                {
                    await method.Create(_services).RunAsync(changed.Token);
                }

                // Returned by itself, or nothing chosen: wait for a choice, or the end.
                await Task.Delay(Timeout.InfiniteTimeSpan, changed.Token);
            }
            catch (OperationCanceledException) when (changed.IsCancellationRequested)
            {
                // Stopping, or another way in was chosen.
            }
            finally
            {
                _choice.Changed -= OnChanged;
            }
        }
    }
}
