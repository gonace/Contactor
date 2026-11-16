using Contactor.Boards.Advantech;

namespace Contactor.Tests.Advantech;

public sealed class Adam6000ModbusTests
{
    [Fact]
    public void Read_requests_match_the_ADAM_6000_manual_examples()
    {
        Assert.Equal(new byte[] { 0x01, 0x00, 0x17, 0x00, 0x08 }, Adam6000Modbus.Read(Adam6000Modbus.ReadCoils, 0x0017, 8));
        Assert.Equal(new byte[] { 0x02, 0x00, 0x01, 0x00, 0x08 }, Adam6000Modbus.Read(Adam6000Modbus.ReadDiscreteInputs, 0x0001, 8));
    }

    [Fact]
    public void Relay_write_uses_one_coil_and_standard_on_off_values()
    {
        Assert.Equal(new byte[] { 0x05, 0x00, 0x17, 0xFF, 0x00 }, Adam6000Modbus.WriteRelay(1, true));
        Assert.Equal(new byte[] { 0x05, 0x00, 0x1C, 0x00, 0x00 }, Adam6000Modbus.WriteRelay(6, false));
    }

    [Fact]
    public void Tcp_frame_encodes_and_validates_the_MBAP_header()
    {
        var pdu = new byte[] { 0x01, 0x00, 0x17, 0x00, 0x08 };
        var request = ModbusTcpFrame.Encode(1, 1, pdu);
        Assert.Equal(new byte[] { 0, 1, 0, 0, 0, 6, 1, 1, 0, 0x17, 0, 8 }, request);
        Assert.Equal(request.Length, ModbusTcpFrame.GetFrameLength(request));

        var response = ModbusTcpFrame.Encode(1, 1, [0x01, 0x01, 0x42]);
        Assert.Equal(new byte[] { 0, 1, 0, 0, 0, 4, 1, 1, 1, 0x42 }, response);
        ModbusTcpFrame.ValidateAnswer(response, 1, 1, Adam6000Modbus.ReadCoils);
        Assert.Equal(0x42UL, Adam6000Modbus.Bits(response.AsSpan(ModbusTcpFrame.HeaderLength), Adam6000Modbus.ReadCoils, 8));
    }

    [Fact]
    public void Bit_response_reads_the_low_six_bits_in_channel_order()
    {
        Assert.Equal(0x42UL, Adam6000Modbus.Bits([0x01, 0x01, 0x42], Adam6000Modbus.ReadCoils, 8));
        Assert.Equal(0b000010UL, Adam6000Modbus.Bits([0x01, 0x01, 0x42], Adam6000Modbus.ReadCoils, 6));
    }
}
