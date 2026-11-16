namespace Contactor.Boards.Advantech;

/// <summary>The ADAM-6000 series boards that share an I/O map and Modbus TCP behavior.</summary>
public static class Adam6000Models
{
    public const string Brand = "Advantech";
    public const int DefaultPort = 502;
    public const byte UnitId = 1;
    public static BoardModel Adam6060 { get; } = Create("advantech-adam-6060", "ADAM-6060");
    public static BoardModel Adam6066 { get; } = Create("advantech-adam-6066", "ADAM-6066");
    public static IReadOnlyList<BoardModel> All { get; } = [Adam6060, Adam6066];

    private static BoardModel Create(string id, string name) => new(
        id, Brand, name,
        new BoardCapabilities(RelayCount: 6, InputCount: 6, ReportsChanges: false),
        DefaultPort,
        LoginMaxLength: 0);
}
