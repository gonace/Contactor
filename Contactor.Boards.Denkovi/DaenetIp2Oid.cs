using System.Globalization;

namespace Contactor.Boards.Denkovi;

/// <summary>
/// The closed list of OIDs the app may send to a DAEnetIP2 v2 (<see cref="All"/>): every <see cref="DaenetIp2Request"/>
/// is built from one of these, so the client can't send any other OID, can't walk the tree (it builds no GetNextRequest)
/// and never builds an OID from data. Some reads change the board, and none of them is on the list: <c>cfgReset</c>
/// (x.9.1.10.0, "Read of this OID causes reset", table 9.1), <c>cfgDefault</c> (x.9.1.30.0, "Reading this OID will load
/// factory default settings … and restart", table 9.1) and <c>pctrlRestart</c> (x.9.2.4.0, "Restart target device
/// trough 'TargetRST' pin", Denkovi's MIB). <see cref="Forbidden"/> names them for the tests. Each instance is made here
/// once, at start, from the manual's tables; the constructor is private.
/// </summary>
public sealed class DaenetIp2Oid
{
    /// <summary>
    /// The ICMP monitoring's restart pins, <c>cfgP5DupRST</c>, x.9.1.29.0: read-write, INTEGER(0..255), "Bit mask showing
    /// on which P5 pins to duplicate TargetRST signal", bit 0 = P5.1, "Available from v.4.097" (table 9.1; section 6.10.2:
    /// "Port P5 Restart Pins"). A board without it answers no such name.
    /// </summary>
    public static readonly DaenetIp2Oid RestartPins = new("cfgP5DupRST", Config(29), DaenetIp2Api.MaxPortValue, writable: true);

    // Each port's pins 1 to 8 (tables 9.4 and 9.5), P3 first.
    private static readonly DaenetIp2Oid[] PinOids =
    [
        .. new[] { DaenetIp2Port.P3, DaenetIp2Port.P5 }.SelectMany(port => Enumerable.Range(1, DaenetIp2Api.PinsPerPort).Select(number =>
            new DaenetIp2Oid(
                string.Create(CultureInfo.InvariantCulture, $"pctrl{port}pin{number}"),
                [.. DaenetIp2Api.Enterprise, 9, 2, (int)port, number, 0],
                DaenetIp2Api.High,
                writable: true))),
    ];

    // Each port's whole byte (tables 9.4 and 9.5), P3 first.
    private static readonly DaenetIp2Oid[] PortOids =
    [
        .. new[] { DaenetIp2Port.P3, DaenetIp2Port.P5 }.Select(port =>
            new DaenetIp2Oid($"pctrl{port}byte", [.. DaenetIp2Api.Enterprise, 9, 2, (int)port, DaenetIp2Api.PortByteArc, 0], DaenetIp2Api.MaxPortValue, writable: true)),
    ];

    // The analog inputs of P6, 1 to 8 (table 9.6): read-only.
    private static readonly DaenetIp2Oid[] AnalogOids =
    [
        .. Enumerable.Range(1, DaenetIp2Api.AnalogInputs).Select(input =>
            new DaenetIp2Oid(
                string.Create(CultureInfo.InvariantCulture, $"pctrlP6pin{input}"),
                [.. DaenetIp2Api.Enterprise, 9, 2, DaenetIp2Api.AnalogPortArc, input, 0],
                DaenetIp2Api.AnalogMaxCounts,
                writable: false)),
    ];

    private readonly int[] _arcs;

    private DaenetIp2Oid(string name, int[] arcs, int maxValue, bool writable)
    {
        Name = name;
        _arcs = arcs;
        MaxValue = maxValue;
        Writable = writable;
    }

    /// <summary>Every OID the app may send, each once: the pins and ports of P3 and P5, the analog inputs and the restart pins.</summary>
    public static IReadOnlyList<DaenetIp2Oid> All { get; } = [.. PinOids, .. PortOids, .. AnalogOids, RestartPins];

    /// <summary>
    /// The OIDs whose read or write changes the board: <c>cfgReset</c> (x.9.1.10.0), <c>cfgDefault</c> (x.9.1.30.0) and
    /// <c>pctrlRestart</c> (x.9.2.4.0, and anything under x.9.2.4). None of them is in <see cref="All"/>, and the tests
    /// prove the client can't send them.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> Forbidden { get; } =
    [
        Config(10),
        Config(30),
        [.. DaenetIp2Api.Enterprise, 9, 2, 4],
    ];

    /// <summary>Its name in the manual's tables, e.g. "cfgP5DupRST".</summary>
    public string Name { get; }

    /// <summary>Its arcs, e.g. 1.3.6.1.4.1.42505.9.1.29.0.</summary>
    public IReadOnlyList<int> Arcs => _arcs;

    /// <summary>The largest value it holds: 1 for a pin, 255 for a port or the restart pins, 1023 for an analog input.</summary>
    public int MaxValue { get; }

    /// <summary>Whether the manual says it is read-write; only such an OID goes in a SetRequest.</summary>
    public bool Writable { get; }

    /// <summary>One output pin, <c>pctrlP3pin</c>n or <c>pctrlP5pin</c>n.</summary>
    public static DaenetIp2Oid Pin(DaenetIp2Pin pin)
    {
        if (!Enum.IsDefined(pin.Port) || pin.Number is < 1 or > DaenetIp2Api.PinsPerPort)
        {
            throw new ArgumentOutOfRangeException(nameof(pin), pin, "The board has pins 1 to 8 on P3 and P5.");
        }

        return PinOids[((pin.Port == DaenetIp2Port.P3 ? 0 : 1) * DaenetIp2Api.PinsPerPort) + pin.Number - 1];
    }

    /// <summary>A port's whole byte, <c>pctrlP3byte</c> or <c>pctrlP5byte</c>.</summary>
    public static DaenetIp2Oid Port(DaenetIp2Port port)
    {
        return port switch
        {
            DaenetIp2Port.P3 => PortOids[0],
            DaenetIp2Port.P5 => PortOids[1],
            _ => throw new ArgumentOutOfRangeException(nameof(port), port, "The board has ports P3 and P5."),
        };
    }

    /// <summary>One analog input of P6, 1 to 8, <c>pctrlP6pin</c>n.</summary>
    public static DaenetIp2Oid Analog(int input)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(input, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input, DaenetIp2Api.AnalogInputs);
        return AnalogOids[input - 1];
    }

    /// <summary>Whether <paramref name="oid"/> is one of <see cref="Forbidden"/>, or under one of them.</summary>
    public static bool IsForbidden(IReadOnlyList<int> oid)
    {
        ArgumentNullException.ThrowIfNull(oid);
        return Forbidden.Any(f => oid.Count >= f.Count && oid.Take(f.Count).SequenceEqual(f));
    }

    /// <summary>"cfgP5DupRST (.1.3.6.1.4.1.42505.9.1.29.0)".</summary>
    public override string ToString()
    {
        return $"{Name} ({DaenetIp2Snmp.OidText(_arcs)})";
    }

    // x.9.1.n.0: a configuration setting of table 9.1.
    private static int[] Config(int arc)
    {
        return [.. DaenetIp2Api.Enterprise, 9, 1, arc, 0];
    }
}
