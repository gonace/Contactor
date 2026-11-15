using Contactor.Boards.SmartDen;

namespace Contactor.Tests.SmartDen;

public sealed class SmartDenModbusTests
{
    [Fact]
    public void Should_BuildModbusTcpFrames_WithMbapHeader()
    {
        var read = SmartDenModbus.ReadCoils(1);
        Assert.Equal("00 01 00 00 00 06 00 01 00 00 00 10", SmartDenModbus.Hex(read.Frame));

        var on = SmartDenModbus.WriteCoil(2, 2, true);
        Assert.Equal("00 02 00 00 00 06 00 05 00 01 FF 00", SmartDenModbus.Hex(on.Frame));

        var off = SmartDenModbus.WriteCoil(3, 16, false);
        Assert.Equal("00 03 00 00 00 06 00 05 00 0F 00 00", SmartDenModbus.Hex(off.Frame));
    }

    [Fact]
    public void Should_BuildPulseRegisterWrites_InHundredMillisecondUnits()
    {
        var pulse = SmartDenModbus.Pulse(7, 10, TimeSpan.FromSeconds(3));
        Assert.Equal("00 07 00 00 00 06 00 06 00 09 00 1E", SmartDenModbus.Hex(pulse.Frame));
        Assert.True(SmartDenModels.Pulse.CanDo(TimeSpan.FromMilliseconds(100)));
        Assert.True(SmartDenModels.Pulse.CanDo(TimeSpan.FromMilliseconds(6553500)));
        Assert.False(SmartDenModels.Pulse.CanDo(TimeSpan.FromMilliseconds(150)));
    }

    [Fact]
    public void Should_RefuseRelayNumbersOutsideTheModel()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartDenModbus.WriteCoil(1, 0, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartDenModbus.WriteCoil(1, 17, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartDenModbus.Pulse(1, 1, TimeSpan.FromMilliseconds(150)));
    }
}
