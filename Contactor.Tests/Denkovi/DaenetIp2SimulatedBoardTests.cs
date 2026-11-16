using System.Net;
using System.Net.Sockets;
using Contactor.Boards;
using Contactor.Boards.Denkovi;
using Contactor.Boards.Velleman;

namespace Contactor.Tests.Denkovi;

/// <summary>The simulated DAEnetIP2 v2 over loopback UDP, with raw SNMP datagrams, as the real board answers them.</summary>
public sealed class DaenetIp2SimulatedBoardTests
{
    private const string ReadOnly = DaenetIp2Api.FactoryReadOnlyCommunity;
    private const string ReadWrite = DaenetIp2Api.FactoryReadWriteCommunity;

    // How long a test waits for an answer that must come; a silent one is shown by the answer to the next request.
    private static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(30);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_AnswerTheManualsGetOfPortP5_ByteForByte()
    {
        await using var simulator = Started(DaenetIp2Models.Relays8);
        await simulator.SwitchRelayAsync(1, true);
        await simulator.SwitchRelayAsync(3, true);
        using var app = Client();

        Assert.Equal(DaenetIp2SnmpTests.P5Is5, await AskAsync(app, simulator, DaenetIp2SnmpTests.GetP5));
    }

    [Fact]
    public async Task Should_TakeTheManualsSetOfPinP5Point1_AndEchoIt()
    {
        await using var simulator = Started(DaenetIp2Models.Relays8);
        using var app = Client();

        Assert.Equal(DaenetIp2SnmpTests.SetP5Pin1HighDone, await AskAsync(app, simulator, DaenetIp2SnmpTests.SetP5Pin1High));

        Assert.Equal(1UL, simulator.Relays);
        Assert.Equal(1, simulator.P5);
        Assert.Equal(0, simulator.P3);
    }

    [Fact]
    public async Task Should_PutRelaysOneToEightOnP3_AndNineToSixteenOnP5_OfTheSixteenRelayKit()
    {
        await using var simulator = Started(DaenetIp2Models.Relays16);
        await simulator.SwitchRelayAsync(1, true);
        await simulator.SwitchRelayAsync(10, true);

        Assert.Equal((0b1, 0b10), (simulator.P3, simulator.P5));
        Assert.Equal(ChannelMask.Of([1, 10]), simulator.State.Relays);
    }

    [Fact]
    public async Task Should_PutTheTwoRelayKit_OnP5Point7AndP5Point8()
    {
        await using var simulator = Started(DaenetIp2Models.Relays2);
        using var app = Client();
        await simulator.SwitchRelayAsync(2, true);

        Assert.Equal(0b1000_0000, simulator.P5);
        await AskAsync(app, simulator, Hex(DaenetIp2Snmp.SetPin(1, ReadWrite, new DaenetIp2Pin(DaenetIp2Port.P5, 7), high: true).Message));
        Assert.Equal(0b11UL, simulator.Relays);

        // A pin no relay is on still switches, but no relay shows it.
        await AskAsync(app, simulator, Hex(DaenetIp2Snmp.SetPin(2, ReadWrite, new DaenetIp2Pin(DaenetIp2Port.P5, 1), high: true).Message));
        Assert.Equal(0b1100_0001, simulator.P5);
        Assert.Equal(0b11UL, simulator.Relays);
    }

    [Fact]
    public async Task Should_AnswerAPinsLevel_AndSetAWholePort()
    {
        await using var simulator = Started(DaenetIp2Models.Relays16);
        using var app = Client();

        var set = DaenetIp2Snmp.Set(1, ReadWrite, DaenetIp2Api.PortOid(DaenetIp2Port.P3), 0b1010_0101, 255);
        Assert.Equal(Hex(DaenetIp2Snmp.Response(1, ReadWrite, set.Oid, 0b1010_0101)), await AskAsync(app, simulator, Hex(set.Message)));
        Assert.Equal(0b1010_0101UL, simulator.Relays);

        var pin = DaenetIp2Snmp.Get(2, ReadOnly, DaenetIp2Api.PinOid(new DaenetIp2Pin(DaenetIp2Port.P3, 3)), 1);
        Assert.Equal(Hex(DaenetIp2Snmp.Response(2, ReadOnly, pin.Oid, 1)), await AskAsync(app, simulator, Hex(pin.Message)));
    }

    public static TheoryData<string, int> NotTheBoards { get; } = new()
    {
        // cfgIP, the analog input P6.1, a pin 9 and a pin 0: no such name.
        { Hex(DaenetIp2Snmp.Get(1, ReadOnly, [.. DaenetIp2Api.Enterprise, 9, 1, 1, 0], 255).Message), DaenetIp2Api.NoSuchName },
        { Hex(DaenetIp2Snmp.Get(1, ReadOnly, [.. DaenetIp2Api.Enterprise, 9, 2, 3, 1, 0], 1023).Message), DaenetIp2Api.NoSuchName },
        { Hex(DaenetIp2Snmp.Set(1, ReadWrite, [.. DaenetIp2Api.Enterprise, 9, 2, 2, 9, 0], 1, 1).Message), DaenetIp2Api.NoSuchName },
        { Hex(DaenetIp2Snmp.Set(1, ReadWrite, [.. DaenetIp2Api.Enterprise, 9, 2, 1, 0, 0], 1, 1).Message), DaenetIp2Api.NoSuchName },

        // A pin to 2, a port to 256 or −1: a bad value.
        { Hex(DaenetIp2Snmp.Set(1, ReadWrite, DaenetIp2Api.PinOid(new DaenetIp2Pin(DaenetIp2Port.P5, 1)), 2, 1).Message), DaenetIp2Api.BadValue },
        { Hex(DaenetIp2Snmp.Set(1, ReadWrite, DaenetIp2Api.PortOid(DaenetIp2Port.P5), 256, 255).Message), DaenetIp2Api.BadValue },
        { Hex(DaenetIp2Snmp.Set(1, ReadWrite, DaenetIp2Api.PortOid(DaenetIp2Port.P5), -1, 255).Message), DaenetIp2Api.BadValue },

        // A set with a NULL value, as snmpget sends it.
        {
            "30 2C 02 01 00 04 07 70 72 69 76 61 74 65 A3 1E 02 01 01 02 01 00 02 01 00 30 13 30 11 06 0D 2B 06 01 04 01 82 CC 09 09 02 02 21 00 05 00",
            DaenetIp2Api.BadValue
        },
    };

    [Theory]
    [MemberData(nameof(NotTheBoards))]
    public async Task Should_AnswerAnErrorStatus_ForWhatTheBoardDoesntHaveOrTake(string request, int errorStatus)
    {
        await using var simulator = Started(DaenetIp2Models.Relays16);
        using var app = Client();

        var answer = DaenetIp2Snmp.Decode(DaenetIp2SnmpTests.Bytes(await AskAsync(app, simulator, request)));

        Assert.Equal((DaenetIp2Api.GetResponse, errorStatus, 1), (answer.PduType, answer.ErrorStatus, answer.ErrorIndex));
        Assert.Equal(DaenetIp2Snmp.Decode(DaenetIp2SnmpTests.Bytes(request)).Oid, answer.Oid);
        Assert.Equal(0, simulator.P3 | simulator.P5);
    }

    [Fact]
    public async Task Should_TakeAGetWithEitherCommunity_AndASetOnlyWithTheReadWriteOne()
    {
        await using var simulator = Started(DaenetIp2Models.Relays8);
        using var app = Client();

        Assert.Equal("00", Value(await AskAsync(app, simulator, Hex(DaenetIp2Snmp.GetPort(1, ReadOnly, DaenetIp2Port.P5).Message))));
        Assert.Equal("00", Value(await AskAsync(app, simulator, Hex(DaenetIp2Snmp.GetPort(2, ReadWrite, DaenetIp2Port.P5).Message))));

        // A set with the read-only community gets no answer, and changes nothing.
        await SendAsync(app, simulator, DaenetIp2Snmp.SetPin(3, ReadOnly, new DaenetIp2Pin(DaenetIp2Port.P5, 1), high: true).Message);
        Assert.Equal("01", Value(await AskAsync(app, simulator, Hex(DaenetIp2Snmp.SetPin(4, ReadWrite, new DaenetIp2Pin(DaenetIp2Port.P5, 2), high: true).Message))));
        Assert.Equal(0b10UL, simulator.Relays);
        Assert.Equal(1, simulator.UnansweredCount);
    }

    [Fact]
    public async Task Should_StaySilent_ForAWrongCommunity_AnotherVersion_AndAnythingButGetAndSet()
    {
        await using var simulator = new DaenetIp2Simulator(DaenetIp2Models.Relays8, "apple", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            ReadOnlyCommunity = "pear",
        };
        simulator.Start();
        using var app = Client();

        await SendAsync(app, simulator, DaenetIp2Snmp.GetPort(1, ReadWrite, DaenetIp2Port.P5).Message);
        await SendAsync(app, simulator, DaenetIp2Snmp.SetPin(2, "pear", new DaenetIp2Pin(DaenetIp2Port.P5, 1), high: true).Message);
        await SendAsync(app, simulator, DaenetIp2SnmpTests.Bytes(Hex(DaenetIp2Snmp.GetPort(3, "pear", DaenetIp2Port.P5).Message).Replace("02 01 00 04 04", "02 01 01 04 04", StringComparison.Ordinal)));
        await SendAsync(app, simulator, DaenetIp2SnmpTests.Bytes(Hex(DaenetIp2Snmp.GetPort(4, "pear", DaenetIp2Port.P5).Message).Replace("04 04 70 65 61 72 A0", "04 04 70 65 61 72 A1", StringComparison.Ordinal)));

        // The first answer is to the next good request.
        Assert.Equal(Hex(DaenetIp2Snmp.Response(5, "pear", DaenetIp2Api.PortOid(DaenetIp2Port.P5), 0)), await AskAsync(app, simulator, Hex(DaenetIp2Snmp.GetPort(5, "pear", DaenetIp2Port.P5).Message)));
        Assert.Equal(4, simulator.UnansweredCount);
        Assert.Equal(0UL, simulator.Relays);
    }

    [Fact]
    public async Task Should_SurviveDatagramsItCantRead_AndServeTheNext()
    {
        await using var simulator = Started(DaenetIp2Models.Relays8);
        var traces = new List<BoardTraceEntry>();
        simulator.Traced += (_, e) =>
        {
            lock (traces)
            {
                traces.Add(e);
            }
        };
        using var app = Client();

        await SendAsync(app, simulator, []);
        await SendAsync(app, simulator, [0x30]);
        await SendAsync(app, simulator, [0x30, 0x82, 0xFF, 0xFF, 0x02]);
        await SendAsync(app, simulator, DaenetIp2SnmpTests.Bytes(DaenetIp2SnmpTests.GetP5)[..20]);
        await SendAsync(app, simulator, new byte[DaenetIp2Api.MaxMessageBytes + 1]);
        await SendAsync(app, simulator, new byte[8000]);

        Assert.Equal(Hex(DaenetIp2Snmp.Response(1, ReadOnly, DaenetIp2Api.PortOid(DaenetIp2Port.P5), 0)), await AskAsync(app, simulator, DaenetIp2SnmpTests.GetP5));
        Assert.Equal(6, simulator.UnansweredCount);
        lock (traces)
        {
            Assert.Contains(traces, t => t.Text == "No answer: an unreadable datagram of 1 bytes: The message is cut short." && t.IsError && t.Bytes is null);
        }
    }

    [Fact]
    public async Task Should_DropTheNextRequests_WhenAsked_AndAnswerAgainAfter()
    {
        await using var simulator = Started(DaenetIp2Models.Relays8);
        using var app = Client();
        simulator.DropRequests = 2;

        await SendAsync(app, simulator, DaenetIp2Snmp.GetPort(1, ReadOnly, DaenetIp2Port.P5).Message);
        await SendAsync(app, simulator, DaenetIp2Snmp.GetPort(2, ReadOnly, DaenetIp2Port.P5).Message);
        var answer = await AskAsync(app, simulator, Hex(DaenetIp2Snmp.GetPort(3, ReadOnly, DaenetIp2Port.P5).Message));

        Assert.Equal(3, DaenetIp2Snmp.Decode(DaenetIp2SnmpTests.Bytes(answer)).RequestId);
        Assert.Equal((2, 0), (simulator.UnansweredCount, simulator.DropRequests));
        Assert.Equal(3, simulator.ReceivedRequests.Count);
    }

    [Fact]
    public async Task Should_RefuseASetOfAFailingRelay_WithGenErr()
    {
        await using var simulator = Started(DaenetIp2Models.Relays8);
        simulator.FailRelays = 0b10;
        using var app = Client();

        var answer = DaenetIp2Snmp.Decode(DaenetIp2SnmpTests.Bytes(await AskAsync(app, simulator, Hex(DaenetIp2Snmp.SetPin(1, ReadWrite, new DaenetIp2Pin(DaenetIp2Port.P5, 2), high: true).Message))));

        Assert.Equal(DaenetIp2Api.GenErr, answer.ErrorStatus);
        Assert.Equal(0UL, simulator.Relays);

        // A whole port that holds the relay is refused too.
        answer = DaenetIp2Snmp.Decode(DaenetIp2SnmpTests.Bytes(await AskAsync(app, simulator, Hex(DaenetIp2Snmp.Set(2, ReadWrite, DaenetIp2Api.PortOid(DaenetIp2Port.P5), 255, 255).Message))));
        Assert.Equal(DaenetIp2Api.GenErr, answer.ErrorStatus);
        Assert.Equal(0UL, simulator.Relays);
    }

    [Fact]
    public async Task Should_ListWhereTheAppAskedFrom_UntilTheConnectionsAreDropped()
    {
        await using var simulator = Started(DaenetIp2Models.Relays8);
        using var app = Client();
        var changes = 0;
        simulator.Changed += (_, _) => Interlocked.Increment(ref changes);

        await AskAsync(app, simulator, DaenetIp2SnmpTests.GetP5);

        Assert.Equal([app.LocalEndPoint!.ToString()!], simulator.State.Clients);
        simulator.DropConnections();
        Assert.Empty(simulator.State.Clients);
        Assert.Equal(2, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task Should_NeverTraceTheCommunity()
    {
        await using var simulator = new DaenetIp2Simulator(DaenetIp2Models.Relays8, "s3cret!", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        simulator.Start();
        var traces = new List<BoardTraceEntry>();
        simulator.Traced += (_, e) =>
        {
            lock (traces)
            {
                traces.Add(e);
            }
        };
        using var app = Client();

        await AskAsync(app, simulator, Hex(DaenetIp2Snmp.SetPin(1, "s3cret!", new DaenetIp2Pin(DaenetIp2Port.P5, 1), high: true).Message));
        await SendAsync(app, simulator, DaenetIp2Snmp.SetPin(2, "s3cret?", new DaenetIp2Pin(DaenetIp2Port.P5, 1), high: true).Message);
        await AskAsync(app, simulator, Hex(DaenetIp2Snmp.GetPort(3, "s3cret!", DaenetIp2Port.P5).Message));

        lock (traces)
        {
            Assert.Contains(traces, t => t.Text == "Turn on relay 1 (P5.1)" && t.Direction == BoardTraceDirection.Sent);
            Assert.Contains(traces, t => t.Text == "No answer: the community is wrong for it" && t.IsError);
            Assert.All(traces, t => Assert.DoesNotContain("s3cret", t.Text, StringComparison.Ordinal));
            Assert.All(traces, t => Assert.DoesNotContain(DaenetIp2Snmp.Hex("s3cret"u8), t.Bytes ?? "", StringComparison.Ordinal));
        }

        Assert.All(simulator.ReceivedRequests, r => Assert.DoesNotContain(DaenetIp2Snmp.Hex("s3cret"u8), r, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_HaveNoInputs_AndRefuseRelaysTheKitHasNot()
    {
        await using var simulator = Started(DaenetIp2Models.Relays2);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => simulator.SetInputAsync(1, true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => simulator.SwitchRelayAsync(3, true));
        Assert.True(simulator.HasLogin);
        Assert.Same(DaenetIp2Models.Relays2, simulator.Model);
    }

    [Fact]
    public void Should_RefuseACommunityTheBoardCantTake()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        Assert.Throws<ArgumentException>(() => new DaenetIp2Simulator(DaenetIp2Models.Relays8, "thirteen-char", logger));
        Assert.Throws<ArgumentException>(() => new DaenetIp2Simulator(DaenetIp2Models.Relays8, "abc", logger));
        Assert.Throws<ArgumentException>(() => new DaenetIp2Simulator(DaenetIp2Models.Relays8) { ReadOnlyCommunity = "with space" });
        Assert.Throws<ArgumentException>(() => new DaenetIp2Simulator(Vm201Model.Model));
    }

    private static DaenetIp2Simulator Started(BoardModel model)
    {
        var simulator = new DaenetIp2Simulator(model);
        simulator.Start();
        return simulator;
    }

    private static Socket Client()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private static async Task SendAsync(Socket app, DaenetIp2Simulator simulator, byte[] datagram)
    {
        await app.SendToAsync(datagram, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, simulator.Port), Cancel);
    }

    // Sends a request and returns the next answer, as hex. Datagrams on loopback keep their order.
    private static async Task<string> AskAsync(Socket app, DaenetIp2Simulator simulator, string request)
    {
        await SendAsync(app, simulator, DaenetIp2SnmpTests.Bytes(request));
        var buffer = new byte[DaenetIp2Api.MaxMessageBytes + 1];
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        wait.CancelAfter(AnswerWait);
        var received = await app.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), wait.Token);
        return DaenetIp2Snmp.Hex(buffer.AsSpan(0, received.ReceivedBytes));
    }

    // An answer's value bytes, e.g. "00".
    private static string Value(string answer)
    {
        return answer[^2..];
    }

    private static string Hex(byte[] bytes)
    {
        return DaenetIp2Snmp.Hex(bytes);
    }
}
