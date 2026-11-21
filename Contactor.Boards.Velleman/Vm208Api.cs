using System.Globalization;
using System.Text;

namespace Contactor.Boards.Velleman;

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

    /// <summary>
    /// What the app knows of the board's own rules: it reads each relay's week schedule and turns it off. It sees a pulse
    /// or a timer started on the board's own page in the status (<c>pulseActive</c>, <c>timerActive</c>), which lives only
    /// in the board's memory and can switch a relay hours later (<c>src/PulseAndTimer.cpp</c>), but can't turn it off
    /// (<see cref="Boards.OwnRules.CantTurnOff"/>). The status can miss one: the board hands each change of those flags
    /// to whichever answer reads it first, and its answer to <c>/mosfet</c> (the transistor outputs, on its own page)
    /// takes it without showing it. So a person should still check the board's own page.
    /// </summary>
    public static OwnRulesAbility OwnRules { get; } = new(Reads: true, TurnsOff: true, MayHaveUnseen: true);

    public const int RelayCount = 4;

    /// <summary>
    /// How many times a request is sent again when the board answered busy (<c>{"Channels":[]}</c>: it couldn't take its
    /// I/O lock within 1 s), and how long it waits before each. A relay request sets a state, never toggles it, so
    /// sending it again is harmless.
    /// </summary>
    public const int BusyRetries = 2;

    /// <inheritdoc cref="BusyRetries"/>
    public static readonly TimeSpan BusyWait = TimeSpan.FromMilliseconds(250);

    /// <summary>The one digital input, <c>getUserInputState()</c> (pin P06 of the card's TCA6424A).</summary>
    public const int InputCount = 1;

    /// <summary>
    /// The largest answer the app reads. The status of the card is about 250 bytes with the factory names; a name
    /// someone typed on the board's page has no length limit in the firmware (<c>Configuration::setName</c>,
    /// <c>src/config_vm208.cpp</c>), so there is room for long ones, but anything far bigger isn't a VM208.
    /// </summary>
    public const int MaxResponseBytes = 8192;

    /// <summary>
    /// <c>GET /getalarms?channel=N</c>: relay N's week schedule, its 14 alarms (<see cref="Vm208Alarm"/>), as
    /// <c>{"Channel":{"alarms":[{"dow":d,"hour":h,"minute":m,"onoff":b,"enabled":b} ×14]}}</c>.
    /// <c>src/Network/server_VM208.cpp</c> <c>server.on("/getalarms")</c>, lines 231–281 (master, read 2026-11-18). The
    /// handler reads its first parameter by position without a check (none crashes the board) as a 16-bit number and
    /// asks <c>config.getShedule(id - 1)</c> (<c>src/config_vm208.cpp</c>, lines 252–260): channel 0 becomes index
    /// 65535, for which it returns null and the handler crashes on it, and a channel above 4 reads an extension
    /// module's channel, not one of the card's relays. So the app only ever sends <see cref="AlarmsQuery"/>, relays
    /// 1–4.
    /// </summary>
    public const string AlarmsPath = "/getalarms";

    /// <summary>
    /// <c>POST /shedule_set</c> (the firmware's spelling) with relay N and its 14 alarms, 29 parameters read by position
    /// (<see cref="ScheduleForm"/>): sets the relay's whole schedule and saves it to the board's flash
    /// (<c>config.writeAlarm</c>), then answers the board's settings. Any other number of parameters is answered 400.
    /// <c>src/Network/server_VM208.cpp</c> <c>server.on("/shedule_set")</c>, lines 522–572. The relay is read as
    /// <c>toInt() - 1</c> into a 16-bit index with no check, so relay 0 writes far outside the schedule table; the app
    /// only sends relays 1–4. Never <c>POST /setalarm</c> (lines 283–304): it changes one alarm in memory only, and a
    /// restart brings the old one back. Never <c>/stoptimer</c> or <c>/stoppulse</c> (lines 182–211): both toggle the
    /// relay before they stop anything.
    /// </summary>
    public const string ScheduleSetPath = "/shedule_set";

    /// <summary>The alarms of a relay's week schedule: an "on" and an "off" alarm for each day.</summary>
    public const int AlarmCount = 14;

    /// <summary>The query of an <see cref="AlarmsPath"/> request for <paramref name="relay"/>: <c>channel=N</c>, as the board's own page sends it.</summary>
    public static string AlarmsQuery(int relay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(relay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relay, RelayCount);
        return string.Create(CultureInfo.InvariantCulture, $"channel={relay}");
    }

    /// <summary>
    /// The form body of a <see cref="ScheduleSetPath"/> request: <paramref name="relay"/> (1–4) and its 14
    /// <paramref name="alarms"/> as the board's page sends them (<c>data/www/js/shedule.js</c> <c>updateShedule</c>):
    /// <c>relay=N</c>, then for k = 1–14 <c>alarm_time_k=HH:MM</c> and <c>alarm_enable_k=true|false</c>. The
    /// firmware reads them by position: pairs 1–7 are the "on" alarms Monday to Sunday, pairs 8–14 the "off" alarms
    /// Monday to Sunday (the day is pair number % 7), the time as two-digit hour, a character, two-digit minute
    /// (<c>substring(0, 2)</c>, <c>substring(3, 5)</c>), and an alarm is enabled only by the word <c>true</c>.
    /// <paramref name="alarms"/> come in the order <see cref="AlarmsPath"/> gives them (<see cref="Vm208Alarm.IsSchedule"/>).
    /// </summary>
    public static string ScheduleForm(int relay, IReadOnlyList<Vm208Alarm> alarms)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(relay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relay, RelayCount);
        ArgumentNullException.ThrowIfNull(alarms);
        if (!Vm208Alarm.IsSchedule(alarms))
        {
            throw new ArgumentException("A VM208 schedule is 14 alarms in the firmware's order, each at a time of day.", nameof(alarms));
        }

        var form = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"relay={relay}"));
        for (var pair = 1; pair <= AlarmCount; pair++)
        {
            var turnsOn = pair <= 7;
            var alarm = alarms[Vm208Alarm.IndexOf((turnsOn ? pair : pair - 7) % 7, turnsOn)];
            form.Append(CultureInfo.InvariantCulture, $"&alarm_time_{pair}={alarm.Hour:00}%3A{alarm.Minute:00}")
                .Append(CultureInfo.InvariantCulture, $"&alarm_enable_{pair}={(alarm.Enabled ? "true" : "false")}");
        }

        return form.ToString();
    }

    /// <summary>The form body of a <see cref="RelayPath"/> request, in the order the firmware reads it.</summary>
    public static string RelayForm(int relay, bool turnOn)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(relay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relay, RelayCount);
        return $"index={relay}&state={(turnOn ? 1 : 0)}&interface={BaseInterface}&socket={BaseSocket}";
    }
}
