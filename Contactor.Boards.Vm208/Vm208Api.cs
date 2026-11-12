namespace Contactor.Boards.Vm208;

/// <summary>
/// The VM208's web API, as Velleman's firmware implements it (https://github.com/Velleman/VM208, with its library
/// https://github.com/velleman/VM208INT). The board is controlled only through its web server (ESPAsyncWebServer),
/// which never sends anything by itself: the app polls it. Only the card itself is used (interface -1); VM208EX
/// extension modules are left out for now.
/// </summary>
public static class Vm208Api
{
    /// <summary>
    /// <c>GET /status?interface=-1&amp;socket=0</c>: the status JSON of the card itself. <c>src/Network/server_VM208.cpp</c>
    /// <c>server.on("/status")</c> and <c>sendIOState(request, interface, socket)</c>. The handler answers only when
    /// the request has exactly two parameters, and reads them by position, not by name: interface first, then socket.
    /// </summary>
    public const string StatusPath = "/status";

    /// <summary>
    /// <c>POST /relay</c> with the form body <c>index=N&amp;state=0|1&amp;interface=-1&amp;socket=0</c>: switches one
    /// relay and answers the status JSON. <c>src/Network/server_VM208.cpp</c> <c>server.on("/relay")</c>. The four
    /// parameters are read by position (a missing one crashes the board), and an index outside 1–4 overflows the
    /// card's channel array (<c>VM208::turnOnChannel</c>, <c>src/VM208.cpp</c> in VM208INT), so the app only ever sends
    /// all four, with relays 1–4.
    /// </summary>
    public const string RelayPath = "/relay";

    /// <summary>The parameters of a status request, in the order the firmware reads them.</summary>
    public const string StatusQuery = "interface=-1&socket=0";

    /// <summary>The <c>interface</c> value for the card itself (<c>mm.getBaseModule()</c>).</summary>
    public const string BaseInterface = "-1";

    /// <summary>The <c>socket</c> value the board's own page sends with the card itself; the firmware ignores it there.</summary>
    public const string BaseSocket = "0";

    /// <summary>The port of the board's web server: <c>AsyncWebServer server(80)</c> in <c>src/Network/server_VM208.cpp</c>.</summary>
    public const int DefaultPort = 80;

    public const int RelayCount = 4;

    /// <summary>The one digital input, <c>getUserInputState()</c> (pin P06 of the card's TCA6424A).</summary>
    public const int InputCount = 1;

    /// <summary>
    /// The largest answer the app reads. The status of the card is about 250 bytes with the factory names; a name
    /// someone typed on the board's page has no length limit in the firmware (<c>Configuration::setName</c>,
    /// <c>src/config_vm208.cpp</c>), so there is room for long ones, but anything far bigger isn't a VM208.
    /// </summary>
    public const int MaxResponseBytes = 8192;

    /// <summary>The form body of a <see cref="RelayPath"/> request, in the order the firmware reads it.</summary>
    public static string RelayForm(int relay, bool turnOn)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(relay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relay, RelayCount);
        return $"index={relay}&state={(turnOn ? 1 : 0)}&interface={BaseInterface}&socket={BaseSocket}";
    }
}
