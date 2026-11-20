namespace Contactor.Simulator;

/// <summary>The <c>Simulator</c> configuration section: the boards that start with the simulator.</summary>
public sealed class SimulatorOptions
{
    public const string SectionName = "Simulator";

    /// <summary>Where the app reaches the boards when no <see cref="AppAddress"/> is given.</summary>
    public const string DefaultAppAddress = "127.0.0.1";

    /// <summary>
    /// The address the app reaches the boards at, shown on the page, e.g. <c>host.docker.internal</c> when the app runs
    /// in a container (the Aspire run). Only shown; where the boards listen is <see cref="ListenOnAllAddresses"/>.
    /// Empty: 127.0.0.1.
    /// </summary>
    public string AppAddress { get; set; } = "";

    /// <summary>
    /// Whether every board, from the settings or added on the page, listens on all of this computer's addresses
    /// instead of 127.0.0.1 only. The Aspire run sets it: the app's container reaches this computer through its network
    /// interface, not loopback. Off for the tests and the simulator run on its own.
    /// </summary>
    public bool ListenOnAllAddresses { get; set; }

    /// <summary>The boards to start. None given: one VM201 on its own port, 9760, without a login.</summary>
    public List<SimulatorBoardOptions> Boards { get; init; } = [];

    /// <summary>
    /// How many boards may run at once, the ones from the settings included; "Add a board" refuses more. Not set: no
    /// limit. The demo sets it, so no visitor runs more than their share.
    /// </summary>
    public int? MaxBoards { get; set; }
}

/// <summary>One board that starts with the simulator, e.g. <c>Simulator__Boards__0__Port=9760</c>.</summary>
public sealed class SimulatorBoardOptions
{
    /// <summary>The model, by its id in the board catalogue, e.g. <c>vm201</c>.</summary>
    public string Type { get; set; } = "vm201";

    public int Port { get; set; }

    /// <summary>Empty, with an empty <see cref="Password"/>, for a board without a login.</summary>
    public string Username { get; set; } = "";

    /// <summary>Development only, but still never logged.</summary>
    public string Password { get; set; } = "";
}
