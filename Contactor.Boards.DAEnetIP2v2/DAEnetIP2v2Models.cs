using Contactor.Boards;

namespace Contactor.Boards.DAEnetIP2v2;

/// <summary>The 8- and 16-relay kits built around the same DAEnetIP2v2 controller.</summary>
public static class DAEnetIP2v2Models
{
    public const string Brand = "Denkovi";
    public const int DefaultPort = 80;

    public static BoardModel Relay8 { get; } = Create("denkovi-daenetip2v2-8", "DAEnetIP2v2 8-relay", 8);
    public static BoardModel Relay16 { get; } = Create("denkovi-daenetip2v2-16", "DAEnetIP2v2 16-relay", 16);
    public static IReadOnlyList<BoardModel> All { get; } = [Relay8, Relay16];

    public static bool IsSupported(BoardModel model) => All.Contains(model);

    private static BoardModel Create(string id, string name, int relays) =>
        new(id, Brand, name, new BoardCapabilities(relays, 0, ReportsChanges: false, AnalogInputCount: 8), DefaultPort, 64);
}
