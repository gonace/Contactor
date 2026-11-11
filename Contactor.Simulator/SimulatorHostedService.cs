using System.Globalization;
using Microsoft.Extensions.Options;

namespace Contactor.Simulator;

/// <summary>Starts the boards from the settings with the simulator, and stops every board when it stops.</summary>
public sealed class SimulatorHostedService : IHostedService
{
    private readonly SimulatedBoards _boards;
    private readonly SimulatorOptions _options;

    public SimulatorHostedService(SimulatedBoards boards, IOptions<SimulatorOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _boards = boards;
        _options = options.Value;
    }

    /// <summary>The boards that start when the settings name none.</summary>
    public static IReadOnlyList<SimulatorBoardOptions> DefaultBoards { get; } = [new() { Type = "vm201", Port = 9760 }];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var board in _options.Boards.Count > 0 ? _options.Boards : DefaultBoards)
        {
            if (_boards.Add(board.Type, board.Port, board.Username, board.Password, fromSettings: true) is { } error)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture, $"Simulator:Boards: the {board.Type} on port {board.Port} can't start. {error}"));
            }
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _boards.DisposeAsync().ConfigureAwait(false);
    }
}
