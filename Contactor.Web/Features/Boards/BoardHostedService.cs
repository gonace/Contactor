using Contactor.Boards;

namespace Contactor.Web.Features.Boards;

/// <summary>
/// Keeps the board connection open for the app's lifetime, starting at the address and login saved on the Settings
/// screen, if any. A new installation may have no address yet (the setup guide asks for it): then it waits until one
/// is saved, and tries nothing before.
/// </summary>
public sealed class BoardHostedService : BackgroundService
{
    private static readonly TimeSpan AddressCheck = TimeSpan.FromSeconds(1);

    private readonly IBoardConnection _connection;
    private readonly BoardAddressHandler _address;
    private readonly TimeProvider _time;

    // BoardTrace is taken only so it exists, and listens, before the first connection.
    public BoardHostedService(IBoardConnection connection, BoardAddressHandler address, BoardTrace trace, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(trace);
        _connection = connection;
        _address = address;
        _time = time;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _address.ApplySavedAsync(stoppingToken);
        while (!_connection.Endpoint.HasHost)
        {
            await Task.Delay(AddressCheck, _time, stoppingToken);
        }

        await _connection.RunAsync(stoppingToken);
    }
}
