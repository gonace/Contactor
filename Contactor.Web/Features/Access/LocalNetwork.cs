namespace Contactor.Web.Features.Access;

/// <summary>
/// "Only on this network" as the way in: nothing to run. People open the address of the machine the app runs on, so
/// the app is as private as that network.
/// </summary>
public sealed class LocalNetwork : IAccessMethod
{
    public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
