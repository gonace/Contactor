using Contactor.Boards.Advantech;

namespace Contactor.Tests.Advantech;

public sealed class Adam6200ModbusTests
{
    [Fact]
    public void Read_and_write_pdus_match_the_ADAM_6200_manual_map()
    {
        Assert.Equal(new byte[] { 0x01, 0x00, 0x17, 0x00, 0x06 }, Adam6200Modbus.ReadRelays());
        Assert.Equal(new byte[] { 0x05, 0x00, 0x17, 0xFF, 0x00 }, Adam6200Modbus.WriteRelay(1, true));
        Assert.Equal(new byte[] { 0x05, 0x00, 0x1C, 0x00, 0x00 }, Adam6200Modbus.WriteRelay(6, false));
    }

    [Fact]
    public void Read_relay_response_uses_only_six_channels()
    {
        Assert.Equal(0b10_0101UL, Adam6200Modbus.ReadRelayBits([0x01, 0x01, 0xE5]));
        Assert.Throws<InvalidDataException>(() => Adam6200Modbus.ReadRelayBits([0x01, 0x01, 0x01, 0x00]));
    }

    [Fact]
    public void Relay_channels_outside_the_manual_map_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Adam6200Modbus.WriteRelay(0, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => Adam6200Modbus.WriteRelay(7, true));
    }
}
