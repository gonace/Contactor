namespace Contactor.Boards.Vm208;

/// <summary>
/// The Velleman VM208 in the board catalogue. It has no login for the app: the firmware asks for HTTP basic
/// authentication on its own web pages only (<c>serveStatic(...).setAuthentication</c> in
/// <c>src/Network/server_VM208.cpp</c>), never on <c>/status</c> or <c>/relay</c>.
/// </summary>
public static class Vm208Model
{
    public static BoardModel Model { get; } = new(
        "vm208",
        "Velleman VM208",
        Vm208Board.Vm208Capabilities,
        Vm208Api.DefaultPort,
        LoginMaxLength: 0);
}
