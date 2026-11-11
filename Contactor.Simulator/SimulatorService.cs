using Contactor.Boards.Vm201;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Contactor.Simulator;

/// <summary>
/// The <c>Simulator</c> configuration section.
/// </summary>
public sealed class SimulatorOptions
{
    public const string SectionName = "Simulator";

    public int Port { get; set; } = 9760;

    /// <summary>Empty, with an empty <see cref="Password"/>, for a board without login.</summary>
    public string Username { get; set; } = "";

    public string Password { get; set; } = "";
}

/// <summary>
/// Runs a <see cref="Vm201Simulator"/> until the process stops, so the app has a board to talk to in development.
/// </summary>
public sealed class SimulatorService : BackgroundService
{
    private readonly SimulatorOptions _options;
    private readonly ILogger<Vm201Simulator> _logger;

    public SimulatorService(IOptions<SimulatorOptions> options, ILogger<Vm201Simulator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var requiresLogin = _options.Username.Length > 0 || _options.Password.Length > 0;
        await using var simulator = requiresLogin
            ? new Vm201Simulator(_options.Username, _options.Password, _logger)
            : new Vm201Simulator(null, null, _logger);
        simulator.Start(_options.Port);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }
}
