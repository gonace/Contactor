using Contactor.Boards.Denkovi;

namespace Contactor.Tests.Denkovi;

/// <summary>The DAEnetIP2 v2's SNMP requests and answers in plain words, as the Debug screen and the simulator show them.</summary>
public sealed class DaenetIp2TraceTextTests
{
    private static readonly DaenetIp2Layout Relays16 = DaenetIp2Models.Layout(DaenetIp2Models.Relays16);
    private static readonly DaenetIp2Layout Relays2 = DaenetIp2Models.Layout(DaenetIp2Models.Relays2);

    [Theory]
    [InlineData(DaenetIp2Api.GetRequest, "9.2.2.33.0", null, "Ask for port P5")]
    [InlineData(DaenetIp2Api.GetRequest, "9.2.1.33.0", null, "Ask for port P3")]
    [InlineData(DaenetIp2Api.GetRequest, "9.2.2.3.0", null, "Ask for relay 11 (P5.3)")]
    [InlineData(DaenetIp2Api.SetRequest, "9.2.1.3.0", 1, "Turn on relay 3 (P3.3)")]
    [InlineData(DaenetIp2Api.SetRequest, "9.2.2.8.0", 0, "Turn off relay 16 (P5.8)")]
    [InlineData(DaenetIp2Api.SetRequest, "9.2.2.8.0", 7, "Set P5.8 to 7")]
    [InlineData(DaenetIp2Api.SetRequest, "9.2.2.33.0", 255, "Set port P5 to 255")]
    [InlineData(DaenetIp2Api.GetRequest, "9.1.1.0", null, "Ask for .1.3.6.1.4.1.42505.9.1.1.0")]
    [InlineData(DaenetIp2Api.GetRequest, "9.2.3.1.0", null, "Ask for .1.3.6.1.4.1.42505.9.2.3.1.0")]
    [InlineData(DaenetIp2Api.GetRequest, "9.2.2.9.0", null, "Ask for .1.3.6.1.4.1.42505.9.2.2.9.0")]
    [InlineData(DaenetIp2Api.GetRequest, "9.2.2.33.1", null, "Ask for .1.3.6.1.4.1.42505.9.2.2.33.1")]
    [InlineData(DaenetIp2Api.SetRequest, "9.1.1.0", 5, "Set .1.3.6.1.4.1.42505.9.1.1.0 to 5")]
    [InlineData(DaenetIp2Api.SetRequest, "9.2.2.1.0", null, "Set P5.1 to nothing")]
    [InlineData(DaenetIp2Api.GetNextRequest, "9.2.2.33.0", null, "Ask for the OID after .1.3.6.1.4.1.42505.9.2.2.33.0")]
    [InlineData(DaenetIp2Api.GetResponse, "9.2.2.33.0", 5, "A message of type A2 for .1.3.6.1.4.1.42505.9.2.2.33.0")]
    public void Should_SayWhatARequestAsks_OnTheSixteenRelayKit(byte pdu, string arcs, int? value, string text)
    {
        Assert.Equal(text, DaenetIp2TraceText.Request(pdu, Oid(arcs), value, Relays16));
    }

    [Theory]
    [InlineData("9.2.2.7.0", 1, "Turn on relay 1 (P5.7)")]
    [InlineData("9.2.2.8.0", 0, "Turn off relay 2 (P5.8)")]
    [InlineData("9.2.2.1.0", 1, "Set P5.1 high")]
    [InlineData("9.2.1.1.0", 0, "Set P3.1 low")]
    public void Should_NameThePinsNoRelayIsOn_ByThePin(string arcs, int value, string text)
    {
        Assert.Equal(text, DaenetIp2TraceText.Request(DaenetIp2Api.SetRequest, Oid(arcs), value, Relays2));
    }

    [Theory]
    [InlineData(0, "Port P5: all low")]
    [InlineData(255, "Port P5: all high")]
    [InlineData(4, "Port P5: pin 3 high")]
    [InlineData(5, "Port P5: pins 1 and 3 high")]
    [InlineData(0b1100_0001, "Port P5: pins 1, 7 and 8 high")]
    public void Should_SayWhichPinsOfAPortAreHigh(int value, string text)
    {
        Assert.Equal(text, DaenetIp2TraceText.Port(DaenetIp2Port.P5, value));
    }

    [Theory]
    [InlineData(0UL, 16, "Relays: all off")]
    [InlineData(0xFFFFUL, 16, "Relays: all on")]
    [InlineData(0b0101UL, 16, "Relays: 1 and 3 on")]
    [InlineData(1UL << 15, 16, "Relays: 16 on")]
    [InlineData(0b11UL, 2, "Relays: all on")]
    [InlineData(1UL << 2, 2, "Relays: all off")]
    public void Should_SayWhichRelaysAreOn(ulong relays, int count, string text)
    {
        Assert.Equal(text, DaenetIp2TraceText.Relays(relays, count));
    }

    [Theory]
    [InlineData("9.2.1.3.0", 1, "Relay 3 turned on")]
    [InlineData("9.2.2.8.0", 0, "Relay 16 turned off")]
    [InlineData("9.2.2.33.0", 9, "Port P5 set to 9")]
    [InlineData("9.1.1.0", 1, "Done")]
    public void Should_SayWhatAnAnswerConfirms(string arcs, int value, string text)
    {
        Assert.Equal(text, DaenetIp2TraceText.Confirmed(Oid(arcs), value, Relays16));
    }

    [Theory]
    [InlineData(1, "Refused: too big (error 1)")]
    [InlineData(2, "Refused: no such name (error 2)")]
    [InlineData(3, "Refused: a value it doesn't take (error 3)")]
    [InlineData(4, "Refused: read-only (error 4)")]
    [InlineData(5, "Refused: it failed (error 5)")]
    [InlineData(17, "Refused: an error (error 17)")]
    public void Should_NameTheErrorStatuses(int status, string text)
    {
        Assert.Equal(text, DaenetIp2TraceText.Refused(status));
    }

    [Fact]
    public void Should_SayAPinsLevel()
    {
        Assert.Equal("P3.2 is high", DaenetIp2TraceText.Pin(new DaenetIp2Pin(DaenetIp2Port.P3, 2), 1));
        Assert.Equal("P3.2 is low", DaenetIp2TraceText.Pin(new DaenetIp2Pin(DaenetIp2Port.P3, 2), 0));
    }

    // Denkovi's prefix, then the arcs of tables 9.1 to 9.6.
    private static int[] Oid(string arcs)
    {
        return [.. DaenetIp2Api.Enterprise, .. arcs.Split('.').Select(a => int.Parse(a, System.Globalization.CultureInfo.InvariantCulture))];
    }
}
