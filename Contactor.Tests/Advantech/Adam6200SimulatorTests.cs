using System.Net.Sockets;
using Contactor.Boards.Advantech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contactor.Tests.Advantech;

public sealed class Adam6200SimulatorTests
{
    [Fact]
    public async Task Should_RejectAnOverlongWriteWithoutChangingARelay()
    {
        await using var simulator = new Adam6200Simulator(Adam6200Models.Adam6260, NullLogger.Instance, TimeProvider.System);
        simulator.Start();
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", simulator.Port, TestContext.Current.CancellationToken);
        var stream = client.GetStream();
        var malformedPdu = new byte[] { 0x05, 0x00, 0x17, 0xFF, 0x00, 0xAA };
        await stream.WriteAsync(ModbusTcpFrame.Encode(1, 1, malformedPdu), TestContext.Current.CancellationToken);

        var header = new byte[ModbusTcpFrame.HeaderLength];
        await stream.ReadExactlyAsync(header, TestContext.Current.CancellationToken);
        var answer = new byte[ModbusTcpFrame.GetFrameLength(header)];
        header.CopyTo(answer, 0);
        await stream.ReadExactlyAsync(answer.AsMemory(header.Length), TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 0x85, 0x03 }, answer.AsSpan(ModbusTcpFrame.HeaderLength).ToArray());
        Assert.Equal(0UL, simulator.State.Relays.Bits);
    }
}
