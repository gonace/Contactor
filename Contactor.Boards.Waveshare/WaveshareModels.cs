namespace Contactor.Boards.Waveshare;

/// <summary>
/// The Waveshare Modbus PoE ETH relays in the board catalogue. They speak the same Modbus RTU over TCP on port 4196,
/// and have no login: Modbus has none, and the password of the board's web page doesn't guard Modbus. The 16CH and
/// 30CH are from Waveshare's wiki only and are not yet checked on a real board; the wiki's 16CH page is unclear about
/// inputs, so the app takes it as having none.
/// </summary>
public static class WaveshareModels
{
    public static BoardModel Relay8 { get; } = Create("waveshare-8", "Waveshare Modbus PoE ETH Relay", relays: 8, inputs: 0);

    public static BoardModel Relay8B { get; } = Create("waveshare-8b", "Waveshare Modbus PoE ETH Relay (B)", relays: 8, inputs: 8);

    public static BoardModel Relay8C { get; } = Create("waveshare-8c", "Waveshare Modbus PoE ETH Relay (C)", relays: 8, inputs: 8);

    public static BoardModel Relay16 { get; } = Create("waveshare-16", "Waveshare Modbus PoE ETH Relay 16CH", relays: 16, inputs: 0);

    public static BoardModel Relay30 { get; } = Create("waveshare-30", "Waveshare Modbus PoE ETH Relay 30CH", relays: 30, inputs: 0);

    public static IReadOnlyList<BoardModel> All { get; } = [Relay8, Relay8B, Relay8C, Relay16, Relay30];

    /// <summary>
    /// Whether the model has relay control mode registers (0x1000–0x1007), with which its inputs can switch its
    /// relays without the app: the models with inputs, the (B) and the (C).
    /// </summary>
    public static bool HasRelayModes(BoardModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Capabilities.InputCount > 0;
    }

    /// <summary>Whether <paramref name="model"/> is one of these.</summary>
    public static bool IsWaveshare(BoardModel model)
    {
        return All.Contains(model);
    }

    private static BoardModel Create(string id, string name, int relays, int inputs)
    {
        return new BoardModel(id, name, new BoardCapabilities(relays, inputs, ReportsChanges: false), WaveshareApi.DefaultPort, LoginMaxLength: 0);
    }
}
