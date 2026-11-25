namespace Contactor.Boards.Velleman;

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

    /// <summary>
    /// <c>GET /api/relay/pulse?key=K&amp;relay=1,3&amp;time=S</c>: turns the listed relays on and the board turns each off
    /// again by itself after S seconds; answers the status JSON, or 400 for a wrong key. <c>Main/main.c</c>
    /// <c>httpServerUriNotFoundCallback</c> (the <c>"pulse"</c> branch, lines 1065–1091), <c>Main/api.c</c>
    /// <c>parsePulseAPICommand</c> (lines 109–160: <c>time</c> by <c>atoi</c>, in any order), <c>pulse.c</c>
    /// <c>startPulse</c> (lines 31–55: a relay whose pulse already runs is left as it is, else its one-shot FreeRTOS timer
    /// gets <c>periodInSeconds * 1000</c> ticks of 1 ms and the relay goes on), <c>Main/main.c</c> <c>vTimerCallback</c>
    /// (lines 459–470: at the end the relay goes off and its <c>pulse</c> flag false). <c>/api/relay/on</c> and
    /// <c>/off</c> don't stop the timer (<c>Main/relays.c</c> <c>setRelayValue</c>). Not on the board's own API reference
    /// page (<c>resources/www/api_reference.shtml</c>), which lists only auth, on, off, toggle, status and names.
    /// </summary>
    public const string RelayPulsePath = "/api/relay/pulse";

    /// <summary>The shortest pulse in seconds: the board's settings page takes 1 and more (<c>resources/www/settings.shtml</c>), and 0 is a timer period FreeRTOS doesn't take.</summary>
    public const int PulseShortestSeconds = 1;

    /// <summary>
    /// The longest pulse in seconds: <c>startPulse</c> multiplies the seconds by 1000 in a 32-bit <c>int</c>
    /// (<c>pulse.c</c> line 40), which overflows above 2,147,483 s, although the settings page offers up to 4,000,000.
    /// </summary>
    public const int PulseLongestSeconds = 2_147_483;

    /// <summary>
    /// What the app knows of the board's own rules (Issue #619): a pulse started on the board's own page
    /// (<c>resources/www/index.shtml</c> and <c>js/vm204.js</c> <c>sendPulse</c>, then <c>/relayN/pulse</c>,
    /// <c>Main/main.c</c> lines 1151–1169: the index page's ACTIVATE button, for the pulse time set on the settings page,
    /// 1–4,000,000 s, default 60 s) runs in the board's memory and turns its relay off when it ends. The app sees it in
    /// the status's <c>pulse</c> flags, but can't turn it off: the firmware has no route that stops a pulse. The status can miss a short one between two polls, so a person should still check the
    /// board's own page. The board has no schedules of its own.
    /// </summary>
    public static OwnRulesAbility OwnRules { get; } = new(Reads: true, TurnsOff: false, MayHaveUnseen: true);

    /// <summary>The port the board's web server listens on unless someone changed it (<c>Main/VM204.c</c> LoadDefaultSettings).</summary>
    public const int DefaultPort = 80;

    public const int RelayCount = 4;

    public const int InputCount = 4;

    /// <summary>One analog input (manual, December 2016, p. 3), as <c>"analog": n</c> in the status.</summary>
    public const int AnalogInputCount = 1;

    /// <summary>
    /// The highest analog value: the PIC32's 10-bit converter (<c>Main/analog.c</c>; the manual's "between 1 and 1024",
    /// p. 19).
    /// </summary>
    public const int AnalogMaxCounts = 1023;

    /// <summary>
    /// The voltage at the terminal that <see cref="AnalogMaxCounts"/> means: "The maximum voltage that can be sensed is
    /// 6.6 V" (manual, p. 19), and the firmware's <c>n / 1023 × 3.3 × 2</c> (<c>Main/analog.c</c>,
    /// <c>resources/www/js/vm204.js</c> updateIO).
    /// </summary>
    public const decimal AnalogFullScaleVolts = 6.6m;

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

    /// <summary>
    /// The longest key the app takes: the firmware always makes 32 letters and digits (<c>Main/main.c</c>
    /// <c>gen_random(appSettings.key, 32)</c>). A longer one isn't the firmware's, and its queries wouldn't fit
    /// <see cref="MaxQueryLength"/>.
    /// </summary>
    public const int KeyMaxLength = 32;

    /// <summary>
    /// The longest query the app sends: the firmware's parsers <c>strcpy</c> the whole query into a 64-byte
    /// <c>queryString</c> (<c>Main/api.c</c> lines 16, 75, 117 and 216), so 63 characters and the terminating zero. A
    /// longer one would overflow the board's stack.
    /// </summary>
    public const int MaxQueryLength = 63;

    /// <summary>
    /// The largest answer the app reads. The firmware's longest API answer, the status, is about 120 bytes; anything far
    /// bigger isn't a VM204.
    /// </summary>
    public const int MaxResponseBytes = 4096;

    /// <summary>Whether a request's query fits the firmware's 64-byte copy of it (<see cref="MaxQueryLength"/>).</summary>
    public static bool QueryFits(string pathAndQuery)
    {
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        var question = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        return question < 0 || pathAndQuery.Length - question - 1 <= MaxQueryLength;
    }

    /// <summary>Whether a character can go in a user name or password, see <see cref="CredentialSymbols"/>.</summary>
    public static bool IsCredentialCharacter(char c)
    {
        return char.IsAsciiLetterOrDigit(c) || CredentialSymbols.Contains(c, StringComparison.Ordinal);
    }
}
