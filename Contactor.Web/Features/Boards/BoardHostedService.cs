using Contactor.Boards;

namespace Contactor.Web.Features.Boards;

/// <summary>
/// Keeps the board connection open for the app's lifetime, starting at the address and login saved on the Settings
/// screen, if any.
/// </summary>
public sealed class BoardHostedService : BackgroundService
{
    private readonly IBoardConnection _connection;
    private readonly BoardAddressHandler _address;

    // BoardTrace is taken only so it exists, and listens, before the first connection.
    public BoardHostedService(IBoardConnection connection, BoardAddressHandler address, BoardTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        _connection = connection;
        _address = address;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _address.ApplySavedAsync(stoppingToken);
        await _connection.RunAsync(stoppingToken);
    }
}
