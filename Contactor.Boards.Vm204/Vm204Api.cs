namespace Contactor.Boards.Vm204;

/// <summary>
/// The VM204's web API, as Velleman's firmware implements it (https://github.com/Velleman/VM204-Firmware, branch V1.1).
/// The board is controlled only through its web server, which never sends anything by itself: the app polls it.
/// </summary>
public static class Vm204Api
{
    /// <summary>
    /// <c>GET /api/auth?user=U&amp;password=P</c>: <c>{"ApiKey":"…"}</c>, or 400 for a wrong login.
    /// <c>Main/main.c</c> <c>httpServerUriNotFoundCallback</c>, <c>Main/api.c</c> <c>authUser</c> and <c>sendAPIKey</c>.
    /// </summary>
    public const string AuthPath = "/api/auth";

    /// <summary>
    /// <c>GET /api/status?key=K</c>: the status JSON, or 400 for a wrong key.
    /// <c>Main/main.c</c> <c>httpServerUriNotFoundCallback</c> and <c>sendIoStatus</c>, <c>Main/api.c</c>
    /// <c>isAPIAuthorized</c>.
    /// </summary>
    public const string StatusPath = "/api/status";

    /// <summary>
    /// <c>GET /api/relay/on?key=K&amp;relay=1,3</c>: turns the listed relays on and answers the status JSON, or 400 for a
    /// wrong key. <c>Main/main.c</c> <c>httpServerUriNotFoundCallback</c>, <c>Main/api.c</c> <c>parseRelayAPICommand</c>,
    /// <c>Main/relays.c</c> <c>setRelayValue</c> (relays 1–4; other numbers are ignored).
    /// </summary>
    public const string RelayOnPath = "/api/relay/on";

    /// <summary><c>GET /api/relay/off?key=K&amp;relay=1,3</c>: as <see cref="RelayOnPath"/>, turning them off.</summary>
    public const string RelayOffPath = "/api/relay/off";

    /// <summary>The port the board's web server listens on unless someone changed it (<c>Main/VM204.c</c> LoadDefaultSettings).</summary>
    public const int DefaultPort = 80;

    public const int RelayCount = 4;

    public const int InputCount = 4;

    /// <summary>
    /// The longest user name or password the API takes: <c>authUser</c> copies each value into a 32-byte buffer
    /// (<c>Main/api.c</c>), and the board keeps them in 32 bytes (<c>Main/settings.h</c> AuthSettings).
    /// </summary>
    public const int CredentialMaxLength = 31;

    /// <summary>
    /// The longest user name and password together: <c>authUser</c> copies the whole query, <c>user=U&amp;password=P</c>,
    /// into a 64-byte buffer (<c>Main/api.c</c>), so 15 characters of names and a terminating zero leave 48.
    /// </summary>
    public const int CredentialsTotalMaxLength = 48;

    /// <summary>
    /// The symbols a user name or password may hold besides letters a–z and digits. The firmware compares the query
    /// string as it arrives, without percent-decoding it (<c>cyclone_tcp/http/http_server_misc.c</c> decodes only the
    /// path), so a value must be sent as it is: only characters a URI keeps unchanged in a query, and never
    /// <c>&amp;</c> or <c>=</c>, which the firmware splits on.
    /// </summary>
    public const string CredentialSymbols = "-._~!$'()*+,;:@";

    /// <summary>The longest key the app takes. The firmware makes 32 letters and digits (<c>Main/main.c</c> gen_random).</summary>
    public const int KeyMaxLength = 64;

    /// <summary>
    /// The largest answer the app reads. The firmware's longest API answer, the status, is about 120 bytes; anything far
    /// bigger isn't a VM204.
    /// </summary>
    public const int MaxResponseBytes = 4096;

    /// <summary>Whether a character can go in a user name or password, see <see cref="CredentialSymbols"/>.</summary>
    public static bool IsCredentialCharacter(char c)
    {
        return char.IsAsciiLetterOrDigit(c) || CredentialSymbols.Contains(c, StringComparison.Ordinal);
    }
}
