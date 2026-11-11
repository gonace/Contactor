using Contactor.Boards;
using Contactor.Boards.Vm201;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Contactor.Tests.Vm201;

/// <summary>
/// The VM201's traffic in plain words, as the Debug screen and the toasts show it.
/// </summary>
public sealed class Vm201TrafficTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    private readonly Vm201Traffic _traffic = new(new Vm201Client(
        Options.Create(new Vm201Options { Host = "127.0.0.1" }),
        TimeProvider.System,
        NullLogger<Vm201Client>.Instance));

    public void Dispose()
    {
        _traffic.Dispose();
    }

    [Theory]
    [InlineData(Vm201TraceKind.Sent, Vm201Command.On, 0x04, "Turn on relay 3")]
    [InlineData(Vm201TraceKind.Sent, Vm201Command.Off, 0xFF, "Turn off all")]
    [InlineData(Vm201TraceKind.Sent, Vm201Command.On, 0x15, "Turn on relay 1, 3 and 5")]
    [InlineData(Vm201TraceKind.Sent, Vm201Command.TimerDisable, 0xFF, "Turn off the board's own timers for all")]
    [InlineData(Vm201TraceKind.Sent, Vm201Command.Toggle, 0x01, "Command T")]
    public void Should_DescribeACommand(Vm201TraceKind kind, byte command, byte mask, string expected)
    {
        var (text, hex, isError) = Vm201TraceText.Describe(Packet(kind, command, mask));

        Assert.Equal(expected, text);
        Assert.False(isError);
        Assert.Equal(Hex(Vm201PacketCodec.Encode(command, mask)), hex);
    }

    [Fact]
    public void Should_DescribeTheVerifiedExampleWithItsBytes()
    {
        var (text, hex, _) = Vm201TraceText.Describe(Packet(Vm201TraceKind.Sent, Vm201Command.On, 0x01));

        Assert.Equal(("Turn on relay 1", "02 06 4F 01 A8 03"), (text, hex));
    }

    [Theory]
    [InlineData(0x00, 0x00, 0x00, "Status: all off · input off")]
    [InlineData(0x05, 0x00, 0x01, "Status: relay 1 and 3 on · input on")]
    [InlineData(0x80, 0x02, 0x00, "Status: relay 8 on · the board's timers on for relay 2 · input off")]
    public void Should_DescribeAStatus(byte outputs, byte timers, byte input, string expected)
    {
        var (text, _, isError) = Vm201TraceText.Describe(Packet(Vm201TraceKind.Received, Vm201Command.Status, outputs, timers, input));

        Assert.Equal(expected, text);
        Assert.False(isError);
    }

    [Fact]
    public void Should_ShowAWrongLoginAsAnError()
    {
        var (text, _, isError) = Vm201TraceText.Describe(Packet(Vm201TraceKind.Received, Vm201Command.AccessDenied));

        Assert.Equal("Wrong user name or password", text);
        Assert.True(isError);
    }

    [Fact]
    public void Should_NeverShowTheLogin()
    {
        var frame = Vm201PacketCodec.Encode(Vm201Command.Password, Vm201PacketCodec.EncodeCredential("secret"));
        var entry = new Vm201TraceEntry(Now, Vm201TraceKind.Sent, frame.AsMemory(0, 3), null);

        var (text, hex, _) = Vm201TraceText.Describe(entry);

        Assert.Equal("Password (hidden)", text);
        Assert.Equal("02 0E 57 …", hex);
    }

    [Theory]
    [InlineData(Vm201TraceKind.Connecting, "192.168.1.50:9760", "Connecting to 192.168.1.50:9760", false)]
    [InlineData(Vm201TraceKind.ConnectionLost, "Connection refused", "Connection lost: Connection refused", true)]
    [InlineData(Vm201TraceKind.ConnectionLost, Vm201Client.StoppedByApp, "The app closed the connection", false)]
    [InlineData(Vm201TraceKind.Reconnecting, "4", "Trying again in 4 s", false)]
    [InlineData(Vm201TraceKind.DiscardedBytes, "3", "Skipped 3 broken bytes from the board", true)]
    public void Should_DescribeAConnectionStep(Vm201TraceKind kind, string detail, string expected, bool error)
    {
        var (text, hex, isError) = Vm201TraceText.Describe(new Vm201TraceEntry(Now, kind, default, detail));

        Assert.Equal((expected, null, error), (text, hex, isError));
    }

    [Fact]
    public void Should_DescribeMalformedPacketsWithoutFailing()
    {
        var empty = new Vm201TraceEntry(Now, Vm201TraceKind.Received, new byte[] { 0x02 }, null);
        var shortStatus = new Vm201TraceEntry(Now, Vm201TraceKind.Received, Vm201PacketCodec.Encode(Vm201Command.Status, 0x01), null);

        Assert.Equal("Unknown answer 0x00", Vm201TraceText.Describe(empty).Text);
        Assert.Equal("Unknown answer S", Vm201TraceText.Describe(shortStatus).Text);
    }

    [Fact]
    public void Should_MarkTheKeepAliveAsRoutine()
    {
        Vm201TraceEntry[] steps =
        [
            Packet(Vm201TraceKind.Received, Vm201Command.Name, 1, 0x41),
            Packet(Vm201TraceKind.Received, Vm201Command.Status, 0x01, 0, 0),
            Packet(Vm201TraceKind.Sent, Vm201Command.StatusRequest),
            Packet(Vm201TraceKind.Received, Vm201Command.Status, 0x01, 0, 0),
            Packet(Vm201TraceKind.Received, Vm201Command.Status, 0x03, 0, 0),
            new Vm201TraceEntry(Now, Vm201TraceKind.Connecting, default, "192.168.1.50:9760"),
            Packet(Vm201TraceKind.Received, Vm201Command.Status, 0x03, 0, 0),
        ];

        Assert.Equal([true, false, true, true, false, false, false], steps.Select(s => _traffic.ToEntry(s).IsRoutine));
    }

    [Theory]
    [InlineData(Vm201TraceKind.Sent, BoardTraceDirection.Sent)]
    [InlineData(Vm201TraceKind.Received, BoardTraceDirection.Received)]
    [InlineData(Vm201TraceKind.Connecting, BoardTraceDirection.None)]
    [InlineData(Vm201TraceKind.ConnectionLost, BoardTraceDirection.None)]
    public void Should_TellWhichWayAStepWent(Vm201TraceKind kind, BoardTraceDirection direction)
    {
        var entry = kind is Vm201TraceKind.Sent or Vm201TraceKind.Received
            ? Packet(kind, Vm201Command.StatusRequest)
            : new Vm201TraceEntry(Now, kind, default, "x");

        Assert.Equal(direction, _traffic.ToEntry(entry).Direction);
    }

    [Fact]
    public void Should_PassEachStepOn_AsTheAppShowsIt()
    {
        var entry = _traffic.ToEntry(Packet(Vm201TraceKind.Sent, Vm201Command.On, 0x04));

        Assert.Equal(new BoardTraceEntry(Now, BoardTraceDirection.Sent, "Turn on relay 3", "02 06 4F 04 A5 03", false, false), entry);
    }

    private static Vm201TraceEntry Packet(Vm201TraceKind kind, byte command, params byte[] data)
    {
        return new Vm201TraceEntry(Now, kind, Vm201PacketCodec.Encode(command, data), null);
    }

    private static string Hex(byte[] bytes)
    {
        return string.Join(' ', bytes.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
    }
}
