using Contactor.Boards.Vm201;

namespace Contactor.Tests.Vm201;

public sealed class Vm201PacketCodecTests
{
    // Verified example from the protocol notes: turn on relay 1.
    private static readonly byte[] TurnOnRelay1 = [0x02, 0x06, 0x4F, 0x01, 0xA8, 0x03];

    [Fact]
    public void Should_EncodeVerifiedExample_WhenTurningOnRelay1()
    {
        var packet = Vm201PacketCodec.Encode(Vm201Command.On, Vm201Channel.Mask(1));

        Assert.Equal(TurnOnRelay1, packet);
    }

    [Fact]
    public void Should_ComputeTwosComplementChecksum()
    {
        Assert.Equal(0xA8, Vm201PacketCodec.Checksum([0x02, 0x06, 0x4F, 0x01]));
        Assert.Equal(0x00, Vm201PacketCodec.Checksum([]));
        Assert.Equal(0x01, Vm201PacketCodec.Checksum([0xFF]));
    }

    [Fact]
    public void Should_EncodeFramingOnly_WhenThereIsNoData()
    {
        var packet = Vm201PacketCodec.Encode(Vm201Command.StatusRequest);

        Assert.Equal([0x02, 0x05, 0x52, 0xA7, 0x03], packet);
    }

    [Fact]
    public void Should_Reject_WhenDataDoesNotFitInOnePacket()
    {
        Assert.Throws<ArgumentException>(() => Vm201PacketCodec.Encode(Vm201Command.Name, new byte[251]));
    }

    [Fact]
    public void Should_DecodeVerifiedExample()
    {
        var packets = new Vm201PacketDecoder().Feed(TurnOnRelay1);

        var packet = Assert.Single(packets);
        Assert.Equal(Vm201Command.On, packet.Command);
        Assert.Equal([0x01], packet.Data.ToArray());
    }

    [Fact]
    public void Should_DecodePacket_WhenItArrivesOneByteAtATime()
    {
        var decoder = new Vm201PacketDecoder();
        var status = Vm201PacketCodec.Encode(Vm201Command.Status, [0x05, 0x00, 0x01]);

        List<Vm201Packet> packets = [];
        foreach (var b in status)
        {
            packets.AddRange(decoder.Feed([b]));
        }

        var packet = Assert.Single(packets);
        Assert.Equal(Vm201Command.Status, packet.Command);
        Assert.Equal([0x05, 0x00, 0x01], packet.Data.ToArray());
    }

    [Fact]
    public void Should_DecodeEveryPacket_WhenSeveralArriveInOneRead()
    {
        byte[] stream =
        [
            .. Vm201PacketCodec.Encode(Vm201Command.LoggedIn),
            .. Vm201PacketCodec.Encode(Vm201Command.Status, [0x01, 0x00, 0x00]),
            .. Vm201PacketCodec.Encode(Vm201Command.Status, [0x03, 0x00, 0x00]),
        ];

        var packets = new Vm201PacketDecoder().Feed(stream);

        Assert.Equal([Vm201Command.LoggedIn, Vm201Command.Status, Vm201Command.Status], packets.Select(p => p.Command));
        Assert.Equal(0x03, packets[2].Data.Span[0]);
    }

    [Fact]
    public void Should_DecodePackets_WhenSplitAcrossReadsAtEveryPosition()
    {
        byte[] stream =
        [
            .. Vm201PacketCodec.Encode(Vm201Command.Name, NameData(1, "Relay 1")),
            .. Vm201PacketCodec.Encode(Vm201Command.Status, [0x80, 0x00, 0x00]),
        ];

        for (var split = 0; split <= stream.Length; split++)
        {
            var decoder = new Vm201PacketDecoder();
            List<Vm201Packet> packets = [.. decoder.Feed(stream.AsSpan(0, split)), .. decoder.Feed(stream.AsSpan(split))];

            Assert.Equal([Vm201Command.Name, Vm201Command.Status], packets.Select(p => p.Command));
            Assert.Equal(0, decoder.DiscardedByteCount);
        }
    }

    [Fact]
    public void Should_DiscardPacketAndKeepReading_WhenChecksumIsWrong()
    {
        var bad = Vm201PacketCodec.Encode(Vm201Command.Status, [0x01, 0x00, 0x00]);
        bad[^2] ^= 0xFF;
        byte[] stream = [.. bad, .. Vm201PacketCodec.Encode(Vm201Command.Status, [0x02, 0x00, 0x00])];

        var decoder = new Vm201PacketDecoder();
        var packets = decoder.Feed(stream);

        var packet = Assert.Single(packets);
        Assert.Equal(0x02, packet.Data.Span[0]);
        Assert.Equal(bad.Length, decoder.DiscardedByteCount);
    }

    [Fact]
    public void Should_DiscardPacket_WhenEtxIsMissing()
    {
        var bad = Vm201PacketCodec.Encode(Vm201Command.LoggedIn);
        bad[^1] = 0x00;
        byte[] stream = [.. bad, .. Vm201PacketCodec.Encode(Vm201Command.Closed)];

        var packet = Assert.Single(new Vm201PacketDecoder().Feed(stream));

        Assert.Equal(Vm201Command.Closed, packet.Command);
    }

    [Fact]
    public void Should_SkipGarbage_BeforeAndBetweenPackets()
    {
        byte[] stream =
        [
            0x00, 0xFF, 0x41,
            .. Vm201PacketCodec.Encode(Vm201Command.LoggedIn),
            0x03, 0x03,
            .. Vm201PacketCodec.Encode(Vm201Command.Closed),
        ];

        var decoder = new Vm201PacketDecoder();
        var packets = decoder.Feed(stream);

        Assert.Equal([Vm201Command.LoggedIn, Vm201Command.Closed], packets.Select(p => p.Command));
        Assert.Equal(5, decoder.DiscardedByteCount);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x04)]
    [InlineData(0x17)]
    [InlineData(0xFF)]
    public void Should_NotWaitForBytes_WhenStrayStxHasImpossibleLength(byte length)
    {
        byte[] stream = [0x02, length, .. Vm201PacketCodec.Encode(Vm201Command.LoggedIn)];

        var packet = Assert.Single(new Vm201PacketDecoder().Feed(stream));

        Assert.Equal(Vm201Command.LoggedIn, packet.Command);
    }

    [Fact]
    public void Should_NotWaitForBytes_WhenLengthDoesNotMatchKnownCommand()
    {
        // A status is always 8 bytes; a stray STX claiming a 20-byte status must not swallow the next packet.
        byte[] stream = [0x02, 0x14, Vm201Command.Status, .. Vm201PacketCodec.Encode(Vm201Command.LoggedIn)];

        var packet = Assert.Single(new Vm201PacketDecoder().Feed(stream));

        Assert.Equal(Vm201Command.LoggedIn, packet.Command);
    }

    [Fact]
    public void Should_StayBounded_WhenFedALotOfData()
    {
        var decoder = new Vm201PacketDecoder();
        var status = Vm201PacketCodec.Encode(Vm201Command.Status, [0x01, 0x00, 0x00]);
        var chunk = new byte[10_000];
        for (var i = 0; i + status.Length <= chunk.Length; i += status.Length)
        {
            status.CopyTo(chunk, i);
        }

        var packets = decoder.Feed(chunk);

        Assert.Equal(chunk.Length / status.Length, packets.Count);
    }

    [Fact]
    public void Should_PadCredentialToNineBytes()
    {
        var data = Vm201PacketCodec.EncodeCredential("admin");

        Assert.Equal([(byte)'a', (byte)'d', (byte)'m', (byte)'i', (byte)'n', 0, 0, 0, 0], data);
        Assert.Equal(14, Vm201PacketCodec.Encode(Vm201Command.Username, data).Length);
    }

    [Theory]
    [InlineData("123456789")]
    [InlineData("lösen")]
    [InlineData("a\u0000b")]
    public void Should_RejectCredential_WhenTheBoardCannotRepresentIt(string value)
    {
        Assert.Throws<ArgumentException>(() => Vm201PacketCodec.EncodeCredential(value));
    }

    [Fact]
    public void Should_DecodeTextUpToFirstZero()
    {
        Assert.Equal("Relay 1", Vm201PacketCodec.DecodeText(NameData(1, "Relay 1").AsSpan(1)));
    }

    [Theory]
    [InlineData(1, 0x01)]
    [InlineData(8, 0x80)]
    public void Should_MapChannelToMask(int channel, byte mask)
    {
        Assert.Equal(mask, Vm201Channel.Mask(channel));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(-1)]
    public void Should_RejectChannel_WhenOutOfRange(int channel)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Vm201Channel.Mask(channel));
    }

    private static byte[] NameData(byte channel, string name)
    {
        var data = new byte[Vm201Command.NameDataLength];
        data[0] = channel;
        System.Text.Encoding.ASCII.GetBytes(name, data.AsSpan(1));
        return data;
    }
}
