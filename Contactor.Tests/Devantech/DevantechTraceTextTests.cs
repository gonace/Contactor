using Contactor.Boards.Devantech;

namespace Contactor.Tests.Devantech;

/// <summary>The commands and answers in plain words, as the Debug screen and the simulator show them.</summary>
public sealed class DevantechTraceTextTests
{
    [Fact]
    public void Should_NeverShowThePassword()
    {
        Assert.Equal("79 …", DevantechTraceText.RequestBytes(DevantechProtocol.Password("apple").Bytes));
        Assert.Equal("20 03 00", DevantechTraceText.RequestBytes(DevantechProtocol.Switch(3, on: true).Bytes));
    }
}
