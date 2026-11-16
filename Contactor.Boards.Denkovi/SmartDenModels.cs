namespace Contactor.Boards.Denkovi;

/// <summary>
/// The Denkovi smartDEN IP-16R-MT in the board catalogue: 16 relays and no inputs, Modbus TCP on port 502, and no login:
/// Modbus has none, and the password of the board's web page doesn't guard Modbus. From Denkovi's manual only, not yet
/// checked on a real board.
/// </summary>
public static class SmartDenModels
{
    /// <summary>
    /// The board's own pulse (<see cref="SmartDenApi.PulseRegister"/>): 0.1 s to 6553.5 s in steps of 0.1 s. Before the
    /// model, which takes it.
    /// </summary>
    public static PulseAbility Pulse { get; } = new(SmartDenApi.PulseUnit, SmartDenApi.PulseUnit * SmartDenApi.MaxPulseTime, SmartDenApi.PulseUnit);

    public static BoardModel Ip16RMt { get; } = new(
        "denkovi-smartden-ip-16r-mt",
        DenkoviBrand.Name,
        "smartDEN IP-16R-MT",
        new BoardCapabilities(SmartDenApi.RelayCount, 0, ReportsChanges: false, Pulse: Pulse),
        SmartDenApi.DefaultPort,
        LoginMaxLength: 0);

    public static IReadOnlyList<BoardModel> All { get; } = [Ip16RMt];

    /// <summary>Whether <paramref name="model"/> is one of these.</summary>
    public static bool IsSmartDen(BoardModel model)
    {
        return All.Contains(model);
    }
}
