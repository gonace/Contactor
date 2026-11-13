namespace Contactor.Web.Features.Boards;

/// <summary>
/// Keeps the chosen board's connection open for the app's lifetime (<see cref="BoardSwitch.RunAsync(Func{ActiveBoard, CancellationToken, Task}?, CancellationToken)"/>),
/// starting each board at the address and login saved on the Settings screen, if any. Before a board is chosen it
/// waits; a board without an address yet (the setup guide asks for it) waits until one is saved, and tries nothing
/// before.
/// </summary>
public sealed class BoardHostedService : BackgroundService
{
    private static readonly TimeSpan AddressCheck = TimeSpan.FromSeconds(1);

    private readonly BoardSwitch _switch;
    private readonly BoardAddressHandler _address;
    private readonly TimeProvider _time;

    // BoardTrace is taken only so it exists, and listens, before the first connection.
    public BoardHostedService(BoardSwitch boardSwitch, BoardAddressHandler address, BoardTrace trace, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(trace);
        _switch = boardSwitch;
        _address = address;
        _time = time;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return _switch.RunAsync(PrepareAsync, stoppingToken);
    }

    private async Task PrepareAsync(ActiveBoard board, CancellationToken cancellationToken)
    {
        await _address.ApplySavedAsync(cancellationToken);
        while (!board.Connection.Endpoint.HasHost)
        {
            await Task.Delay(AddressCheck, _time, cancellationToken);
        }
    }
}
