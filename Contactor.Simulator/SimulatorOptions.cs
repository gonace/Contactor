namespace Contactor.Simulator;

/// <summary>The <c>Simulator</c> configuration section: the boards that start with the simulator.</summary>
public sealed class SimulatorOptions
{
    public const string SectionName = "Simulator";

    /// <summary>The boards to start. None given: one VM201 on its own port, 9760, without a login.</summary>
    public List<SimulatorBoardOptions> Boards { get; init; } = [];
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
