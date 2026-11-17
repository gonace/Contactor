namespace Contactor.Boards.Denkovi;

/// <summary>
/// Which output pin drives which relay of a kit: relay n is <see cref="Relays"/>[n − 1]. A pin that is High turns its
/// relay on (manual, figure 15.1).
/// </summary>
public sealed class DaenetIp2Layout
{
    // The relay each pin drives, for RelayOf.
    private readonly Dictionary<DaenetIp2Pin, int> _relayOf;

    public DaenetIp2Layout(IReadOnlyList<DaenetIp2Pin> relays)
    {
        ArgumentNullException.ThrowIfNull(relays);
        if (relays.Count is < 1 or > 2 * DaenetIp2Api.PinsPerPort
            || relays.Any(p => !Enum.IsDefined(p.Port) || p.Number is < 1 or > DaenetIp2Api.PinsPerPort)
            || relays.Distinct().Count() != relays.Count)
        {
            throw new ArgumentException("Each relay needs a pin of its own, 1 to 8 on P3 or P5.", nameof(relays));
        }

        Relays = [.. relays];
        Ports = [.. Relays.Select(p => p.Port).Distinct().Order()];
        _relayOf = Relays.Select((pin, index) => (pin, index)).ToDictionary(p => p.pin, p => p.index + 1);
    }

    /// <summary>The pin of each relay, relay 1 first.</summary>
    public IReadOnlyList<DaenetIp2Pin> Relays { get; }

    /// <summary>The ports the relays are on, P3 before P5: the ones a poll reads.</summary>
    public IReadOnlyList<DaenetIp2Port> Ports { get; }

    /// <summary>The pin of relay <paramref name="relay"/> (1-based).</summary>
    public DaenetIp2Pin PinOf(int relay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(relay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relay, Relays.Count);
        return Relays[relay - 1];
    }

    /// <summary>The relay a pin drives, or null for a pin no relay is on.</summary>
    public int? RelayOf(DaenetIp2Pin pin)
    {
        return _relayOf.TryGetValue(pin, out var relay) ? relay : null;
    }

    /// <summary>The relays that are on (bit 0 = relay 1) for the ports' bytes: a relay is on when its pin is High.</summary>
    public ulong RelaysOf(int p3, int p5)
    {
        ulong relays = 0;
        for (var i = 0; i < Relays.Count; i++)
        {
            var pin = Relays[i];
            var port = pin.Port == DaenetIp2Port.P3 ? p3 : p5;
            if ((port & pin.Bit) != 0)
            {
                relays |= 1UL << i;
            }
        }

        return relays;
    }
}

/// <summary>
/// The Denkovi DAEnetIP2 v2 kits in the board catalogue: the controller with a Denkovi relay board on its outputs
/// (manual, appendix 3). SNMPv1 on UDP port 161, a poll, no digital inputs and no pulse. Every kit has the controller's
/// 8 analog inputs on P6, whatever relay board is on P3 and P5. The login is the board's read-write community, a
/// password alone that the board always has: section 6.6.1 says it is used in read and write operations. From Denkovi's
/// manual only, not yet checked on a real board.
/// </summary>
public static class DaenetIp2Models
{
    /// <summary>
    /// The 8 analog inputs of P6: 10 bit, 0 to 1023, with Vref = 3.3 V (table 2.1: "8 (10bit ADC, Vref=3.3V)"; table
    /// 9.6: INTEGER(0..1023); appendix 11: "ADC works in range of 0-3.3V"). Whether 3.3 V reads 1023 or 1024 counts the
    /// manual doesn't say: the app takes 1023 = 3.3 V. Written before the models, which take it: static properties start
    /// in the order they are written.
    /// </summary>
    public static AnalogAbility Analog { get; } = new(DaenetIp2Api.AnalogInputs, DaenetIp2Api.AnalogMaxCounts, 3.3m);

    /// <summary>The 16-relay board on P3 and P5 (section 12, 3.1): relays 1 to 8 on P3.1 to P3.8, 9 to 16 on P5.1 to P5.8.</summary>
    public static BoardModel Relays16 { get; } = Create("denkovi-daenetip2-v2-16", "DAEnetIP2 v2 16-relay kit", 16);

    /// <summary>The 8-relay board on P5 (section 12, 3.2): relays 1 to 8 on P5.1 to P5.8.</summary>
    public static BoardModel Relays8 { get; } = Create("denkovi-daenetip2-v2-8", "DAEnetIP2 v2 8-relay kit", 8);

    /// <summary>The 2-relay board on P5.7 and P5.8 (section 12, 3.3): relay 1 on P5.7, relay 2 on P5.8.</summary>
    public static BoardModel Relays2 { get; } = Create("denkovi-daenetip2-v2-2", "DAEnetIP2 v2 2-relay kit", 2);

    public static IReadOnlyList<BoardModel> All { get; } = [Relays16, Relays8, Relays2];

    private static readonly DaenetIp2Layout Layout16 = new([.. Port(DaenetIp2Port.P3), .. Port(DaenetIp2Port.P5)]);
    private static readonly DaenetIp2Layout Layout8 = new(Port(DaenetIp2Port.P5));
    private static readonly DaenetIp2Layout Layout2 = new([new(DaenetIp2Port.P5, 7), new(DaenetIp2Port.P5, 8)]);

    /// <summary>Whether <paramref name="model"/> is one of these.</summary>
    public static bool IsDaenetIp2(BoardModel model)
    {
        return All.Contains(model);
    }

    /// <summary>Which pin drives which relay of <paramref name="model"/>.</summary>
    public static DaenetIp2Layout Layout(BoardModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (ReferenceEquals(model, Relays16))
        {
            return Layout16;
        }

        if (ReferenceEquals(model, Relays8))
        {
            return Layout8;
        }

        return ReferenceEquals(model, Relays2) ? Layout2 : throw new ArgumentException($"{model.Name} isn't a DAEnetIP2 v2 kit.", nameof(model));
    }

    /// <summary>
    /// Why the board can't take this community, or null when it can: 4 to 12 characters (table 9.1 and section 6.6.1),
    /// letters, digits and plain symbols without spaces, so each is one byte. Empty is refused, in words of its own: the
    /// board always has a community.
    /// </summary>
    public static LoginProblem? CheckCommunity(string community)
    {
        ArgumentNullException.ThrowIfNull(community);
        if (community.Length == 0)
        {
            return new LoginProblem(LoginProblemKind.CommunityMissing);
        }

        if (community.Length is < DaenetIp2Api.CommunityMinLength or > DaenetIp2Api.CommunityMaxLength)
        {
            return new LoginProblem(LoginProblemKind.CommunityLength, MaxLength: DaenetIp2Api.CommunityMaxLength, MinLength: DaenetIp2Api.CommunityMinLength);
        }

        return community.All(IsCommunityCharacter) ? null : new LoginProblem(LoginProblemKind.PasswordCharacters);
    }

    /// <summary>
    /// Why the app can't send this community, or null when it can: one the board takes (<see cref="CheckCommunity"/>), or
    /// none at all, on a board with no community saved yet, which gets no answer.
    /// </summary>
    public static LoginProblem? CheckSendable(string community)
    {
        ArgumentNullException.ThrowIfNull(community);
        return community.Length == 0 ? null : CheckCommunity(community);
    }

    /// <summary>A printable ASCII character other than a space.</summary>
    public static bool IsCommunityCharacter(char c)
    {
        return c is > ' ' and <= '~';
    }

    private static DaenetIp2Pin[] Port(DaenetIp2Port port)
    {
        return [.. Enumerable.Range(1, DaenetIp2Api.PinsPerPort).Select(n => new DaenetIp2Pin(port, n))];
    }

    private static BoardModel Create(string id, string name, int relays)
    {
        return new BoardModel(
            id,
            DenkoviBrand.Name,
            name,
            new BoardCapabilities(relays, 0, ReportsChanges: false, Analog: Analog, OwnRules: OwnRulesAbility.CantTell),
            DaenetIp2Api.DefaultPort,
            LoginMaxLength: DaenetIp2Api.CommunityMaxLength,
            LoginHint: new LoginHint(
                LoginHintKind.SnmpCommunity,
                DaenetIp2Api.CommunityMaxLength,
                MinLength: DaenetIp2Api.CommunityMinLength,
                FactoryValue: DaenetIp2Api.FactoryReadWriteCommunity),
            PasswordOnly: true,
            LoginRequired: true);
    }
}
