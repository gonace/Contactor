using System.Globalization;

namespace Contactor.Boards.Denkovi;

/// <summary>
/// The DAEnetIP2 v2's SNMP requests and answers in plain words, for the app's Debug screen and the simulator alike, e.g.
/// "Turn on relay 3 (P5.3)". The words name OIDs and values only; the bytes shown below them are the PDU alone, so the
/// community, a secret, is never in a trace.
/// </summary>
public static class DaenetIp2TraceText
{
    // x.9.2.n.p.0: Denkovi's prefix, 9, 2, the port, the pin or 33, and 0.
    private static readonly int PortArcsLength = DaenetIp2Api.Enterprise.Count + 5;

    /// <summary>
    /// What a request asks for: "Ask for port P5", "Ask for analog input 3 (P6.3)", "Turn on relay 3 (P5.3)", "Turn off
    /// relay 3 (P5.3)", "Set P3.2 high", "Set port P5 to 255", "Ask for the restart pins (cfgP5DupRST)", "Set the
    /// restart pins (cfgP5DupRST) to P5.8", or the OID for anything else.
    /// </summary>
    public static string Request(byte pduType, IReadOnlyList<int> oid, int? value, DaenetIp2Layout layout)
    {
        ArgumentNullException.ThrowIfNull(oid);
        ArgumentNullException.ThrowIfNull(layout);
        var target = Target(oid);
        var analog = DaenetIp2Api.AnalogInputOf(oid);
        var restart = oid.SequenceEqual(DaenetIp2Oid.RestartPins.Arcs);
        return pduType switch
        {
            DaenetIp2Api.GetRequest when restart => "Ask for the restart pins (cfgP5DupRST)",
            DaenetIp2Api.SetRequest when restart && value is >= 0 and <= DaenetIp2Api.MaxPortValue => $"Set the restart pins (cfgP5DupRST) to {PinList(value.Value, layout)}",
            DaenetIp2Api.GetRequest when analog is { } input => $"Ask for analog input {Number(input)} (P6.{Number(input)})",
            DaenetIp2Api.SetRequest when analog is { } input => $"Set analog input {Number(input)} (P6.{Number(input)}) to {Value(value)}",
            DaenetIp2Api.GetRequest => target switch
            {
                { Pin: { } pin } => layout.RelayOf(pin) is { } relay ? $"Ask for relay {Number(relay)} ({pin})" : $"Ask for {pin}",
                { Port: { } port } => $"Ask for port {port}",
                _ => $"Ask for {DaenetIp2Snmp.OidText(oid)}",
            },
            DaenetIp2Api.SetRequest => target switch
            {
                { Pin: { } pin } when value is DaenetIp2Api.High or DaenetIp2Api.Low && layout.RelayOf(pin) is { } relay =>
                    $"Turn {(value == DaenetIp2Api.High ? "on" : "off")} relay {Number(relay)} ({pin})",
                { Pin: { } pin } when value is DaenetIp2Api.High or DaenetIp2Api.Low => $"Set {pin} {(value == DaenetIp2Api.High ? "high" : "low")}",
                { Pin: { } pin } => $"Set {pin} to {Value(value)}",
                { Port: { } port } => $"Set port {port} to {Value(value)}",
                _ => $"Set {DaenetIp2Snmp.OidText(oid)} to {Value(value)}",
            },
            DaenetIp2Api.GetNextRequest => $"Ask for the OID after {DaenetIp2Snmp.OidText(oid)}",
            _ => $"A message of type {pduType:X2} for {DaenetIp2Snmp.OidText(oid)}",
        };
    }

    /// <summary>What <paramref name="request"/> asks for, as <see cref="Request(byte, IReadOnlyList{int}, int?, DaenetIp2Layout)"/>.</summary>
    public static string Request(DaenetIp2Request request, DaenetIp2Layout layout)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Request(request.PduType, request.Oid, request.Value, layout);
    }

    /// <summary>
    /// A <c>cfgP5DupRST</c> answer, with the relay on each pin: "Restart pins: P5.7 (relay 1) and P5.8 (relay 2)",
    /// "Restart pins: P5.1" for a pin no relay is on, or "Restart pins: none".
    /// </summary>
    public static string RestartPins(int pins, DaenetIp2Layout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return $"Restart pins: {PinList(pins, layout)}";
    }

    /// <summary>"Port P5: pins 1 and 3 high", "Port P5: all low" or "Port P5: all high".</summary>
    public static string Port(DaenetIp2Port port, int value)
    {
        return $"Port {port}: {HighList(value)}";
    }

    /// <summary>"P5.3 is high" or "P5.3 is low".</summary>
    public static string Pin(DaenetIp2Pin pin, int value)
    {
        return value switch
        {
            DaenetIp2Api.High => $"{pin} is high",
            DaenetIp2Api.Low => $"{pin} is low",
            _ => $"{pin} is {Number(value)}",
        };
    }

    /// <summary>One analog input's value in volts, with the board's count: "Analog input 3: 1.65 V (512)".</summary>
    public static string Analog(int input, int counts, AnalogAbility analog)
    {
        ArgumentNullException.ThrowIfNull(analog);
        return string.Create(CultureInfo.InvariantCulture, $"Analog input {input}: {analog.Volts(counts):0.00} V ({counts})");
    }

    /// <summary>"Relays: all off", "Relays: 1 and 3 on" or "Relays: all on".</summary>
    public static string Relays(ulong relays, int count)
    {
        return $"Relays: {OnList(relays, count)}";
    }

    /// <summary>
    /// What the answer to a set confirms: "Relay 3 turned on", "P3.2 set high", "Port P5 set to 255", "Restart pins set to
    /// P5.8 (relay 2)" or "Done".
    /// </summary>
    public static string Confirmed(IReadOnlyList<int> oid, int value, DaenetIp2Layout layout)
    {
        ArgumentNullException.ThrowIfNull(oid);
        ArgumentNullException.ThrowIfNull(layout);
        if (oid.SequenceEqual(DaenetIp2Oid.RestartPins.Arcs) && value is >= 0 and <= DaenetIp2Api.MaxPortValue)
        {
            return $"Restart pins set to {PinList(value, layout)}";
        }

        return Target(oid) switch
        {
            { Pin: { } pin } when value is DaenetIp2Api.High or DaenetIp2Api.Low && layout.RelayOf(pin) is { } relay =>
                $"Relay {Number(relay)} turned {(value == DaenetIp2Api.High ? "on" : "off")}",
            { Pin: { } pin } when value is DaenetIp2Api.High or DaenetIp2Api.Low => $"{pin} set {(value == DaenetIp2Api.High ? "high" : "low")}",
            { Port: { } port, Pin: null } => $"Port {port} set to {Number(value)}",
            _ => "Done",
        };
    }

    /// <summary>"Refused: no such name (error 2)".</summary>
    public static string Refused(int errorStatus)
    {
        return $"Refused: {ErrorName(errorStatus)}";
    }

    /// <summary>An SNMP error-status in words: "no such name (error 2)".</summary>
    public static string ErrorName(int errorStatus)
    {
        var name = errorStatus switch
        {
            DaenetIp2Api.TooBig => "too big",
            DaenetIp2Api.NoSuchName => "no such name",
            DaenetIp2Api.BadValue => "a value it doesn't take",
            DaenetIp2Api.ReadOnly => "read-only",
            DaenetIp2Api.GenErr => "it failed",
            _ => "an error",
        };
        return $"{name} (error {Number(errorStatus)})";
    }

    /// <summary>The port, and the pin unless it is the port's whole byte, of an OID in tables 9.4 and 9.5; nothing for any other.</summary>
    public static (DaenetIp2Port? Port, DaenetIp2Pin? Pin) Target(IReadOnlyList<int> oid)
    {
        ArgumentNullException.ThrowIfNull(oid);
        var prefix = DaenetIp2Api.Enterprise.Count;
        if (oid.Count != PortArcsLength
            || !oid.Take(prefix).SequenceEqual(DaenetIp2Api.Enterprise)
            || oid[prefix] != 9
            || oid[prefix + 1] != 2
            || oid[prefix + 2] is not ((int)DaenetIp2Port.P3 or (int)DaenetIp2Port.P5)
            || oid[prefix + 4] != 0)
        {
            return (null, null);
        }

        var port = (DaenetIp2Port)oid[prefix + 2];
        var arc = oid[prefix + 3];
        return arc switch
        {
            DaenetIp2Api.PortByteArc => (port, null),
            >= 1 and <= DaenetIp2Api.PinsPerPort => (port, new DaenetIp2Pin(port, arc)),
            _ => (null, null),
        };
    }

    // "P5.7 (relay 1) and P5.8 (relay 2)", "P5.1", "none": the P5 pins of a cfgP5DupRST value, with the relay on each
    // when there is a layout.
    private static string PinList(int pins, DaenetIp2Layout? layout = null)
    {
        List<string> names = [.. new ChannelMask((ulong)(pins & DaenetIp2Api.MaxPortValue)).Channels.Select(number =>
        {
            var pin = new DaenetIp2Pin(DaenetIp2Port.P5, number);
            return layout?.RelayOf(pin) is { } relay ? $"{pin} (relay {Number(relay)})" : pin.ToString();
        })];
        return names.Count switch
        {
            0 => "none",
            1 => names[0],
            _ => $"{string.Join(", ", names[..^1])} and {names[^1]}",
        };
    }

    // "pins 1 and 3 high", "all low", "all high".
    private static string HighList(int value)
    {
        var bits = value & DaenetIp2Api.MaxPortValue;
        if (bits == 0)
        {
            return "all low";
        }

        if (bits == DaenetIp2Api.MaxPortValue)
        {
            return "all high";
        }

        var pins = new ChannelMask((ulong)bits).Channels.Select(Number).ToList();
        return pins.Count == 1
            ? $"pin {pins[0]} high"
            : $"pins {string.Join(", ", pins[..^1])} and {pins[^1]} high";
    }

    // "all off", "1 and 3 on", "all on".
    private static string OnList(ulong bits, int count)
    {
        var all = ChannelMask.FirstN(count).Bits;
        bits &= all;
        if (bits == 0)
        {
            return "all off";
        }

        if (bits == all && count > 1)
        {
            return "all on";
        }

        var channels = new ChannelMask(bits).Channels.Select(Number).ToList();
        return channels.Count == 1
            ? $"{channels[0]} on"
            : $"{string.Join(", ", channels[..^1])} and {channels[^1]} on";
    }

    private static string Value(int? value)
    {
        return value is { } v ? Number(v) : "nothing";
    }

    private static string Number(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
