namespace Contactor.Web.Features.Access;

/// <summary>Keeps the chosen way in running for the app's lifetime, e.g. the Cloudflare tunnel.</summary>
public sealed class AccessHostedService : BackgroundService
{
    private readonly AccessChoice _choice;
    private readonly IServiceProvider _services;

    public AccessHostedService(AccessChoice choice, IServiceProvider services)
    {
        _choice = choice;
        _services = services;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return _choice.Current.Create(_services).RunAsync(stoppingToken);
    }
}
