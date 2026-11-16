namespace Contactor.Boards.Advantech;

/// <summary>The ADAM-6200 relay boards included in Contactor.</summary>
public static class Adam6200Models
{
    public const string Brand = "Advantech";
    public const int DefaultPort = 502;
    public const byte DefaultUnitId = 1;
    public static BoardModel Adam6260 { get; } = new(
        "advantech-adam-6260", Brand, "ADAM-6260",
        new BoardCapabilities(RelayCount: 6, InputCount: 0, ReportsChanges: false),
        DefaultPort,
        LoginMaxLength: 0);
    public static IReadOnlyList<BoardModel> All { get; } = [Adam6260];
}
