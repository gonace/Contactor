using System.Text;
using Contactor.Boards.Vm208;

namespace Contactor.Tests.Vm208;

/// <summary>The board's status JSON: what the firmware writes is read, anything else refused, never half-read.</summary>
public sealed class Vm208JsonTests
{
    // As sendIOState (src/Network/server_VM208.cpp) writes it with ArduinoJson 5: compact, the factory names.
    private const string FirmwareStatus =
        "{\"Channels\":[{\"name\":\"RELAY1\",\"state\":true,\"pulseActive\":false,\"timerActive\":false},"
        + "{\"name\":\"RELAY2\",\"state\":false,\"pulseActive\":false,\"timerActive\":false},"
        + "{\"name\":\"RELAY3\",\"state\":true,\"pulseActive\":false,\"timerActive\":false},"
        + "{\"name\":\"RELAY4\",\"state\":false,\"pulseActive\":false,\"timerActive\":false}],"
        + "\"m1\":false,\"m2\":true,\"input\":true}";

    [Fact]
    public void Should_ReadTheStatus_AsTheFirmwareWritesIt()
    {
        Assert.Equal(new Vm208Status(0b0101, Input: true, Outputs: 0b10), Vm208Json.ReadStatus(Bytes(FirmwareStatus)));
    }

    [Fact]
    public void Should_WriteTheStatus_AsTheFirmwareDoes()
    {
        Assert.Equal(FirmwareStatus, Vm208Json.WriteStatus(new Vm208Status(0b0101, Input: true, Outputs: 0b10)));
    }

    [Fact]
    public void Should_IgnoreTheNamesAndFlags_WhateverTheyHold()
    {
        // Names are typed on the board's own page: quotes, non-ASCII and bytes that aren't UTF-8 must not matter.
        var written = Vm208Json.WriteStatus(new Vm208Status(0b1000, false, 0), ["Fläkt \"1\"", "\\", "", new string('x', 1000)]);
        Assert.Equal(new Vm208Status(0b1000, false, 0), Vm208Json.ReadStatus(Bytes(written)));

        var broken = Bytes(FirmwareStatus.Replace("RELAY2", "RÿX", StringComparison.Ordinal));
        broken[Array.IndexOf(broken, (byte)0xC3)] = 0xFF;
        Assert.Equal(0b0101, Vm208Json.ReadStatus(broken).Relays);

        var odd = FirmwareStatus.Replace("\"pulseActive\":false", "\"pulseActive\":1", StringComparison.Ordinal)
            .Replace("\"m2\":true", "\"m2\":\"on\"", StringComparison.Ordinal);
        Assert.Equal(new Vm208Status(0b0101, true, 0), Vm208Json.ReadStatus(Bytes(odd)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Channels\":{},\"input\":false}")]
    [InlineData("{\"Channels\":[{\"state\":true},{\"state\":true},{\"state\":true}],\"input\":false}")]
    [InlineData("{\"Channels\":[{\"state\":true},{\"state\":true},{\"state\":true},{\"state\":true},{\"state\":true}],\"input\":false}")]
    [InlineData("{\"Channels\":[{\"state\":1},{\"state\":true},{\"state\":true},{\"state\":true}],\"input\":false}")]
    [InlineData("{\"Channels\":[{},{\"state\":true},{\"state\":true},{\"state\":true}],\"input\":false}")]
    [InlineData("{\"Channels\":[true,true,true,true],\"input\":false}")]
    [InlineData("{\"Channels\":[{\"state\":true},{\"state\":true},{\"state\":true},{\"state\":true}]}")]
    [InlineData("{\"Channels\":[{\"state\":true},{\"state\":true},{\"state\":true},{\"state\":true}],\"input\":\"true\"}")]
    [InlineData("{\"Channels\":[{\"state\":true},{\"state\":true},{\"state\":true},{\"state\":true}],\"input\":false")]
    [InlineData("[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]")]
    public void Should_RefuseAnythingElse(string json)
    {
        Assert.Throws<Vm208ProtocolException>(() => Vm208Json.ReadStatus(Bytes(json)));
    }

    [Fact]
    public void Should_SaySo_WhenTheBoardWasTooBusyToSendItsRelays()
    {
        // sendIOState sends only the empty array when it can't take its I/O lock within a second.
        var error = Assert.Throws<Vm208ProtocolException>(() => Vm208Json.ReadStatus(Bytes("{\"Channels\":[]}")));

        Assert.Equal("The VM208 was busy and sent no relays.", error.Message);
    }

    [Fact]
    public void Should_RefuseAnAnswerLongerThanTheCap()
    {
        var huge = FirmwareStatus.Replace("RELAY1", new string('x', Vm208Api.MaxResponseBytes), StringComparison.Ordinal);

        Assert.Throws<Vm208ProtocolException>(() => Vm208Json.ReadStatus(Bytes(huge)));
    }

    private static byte[] Bytes(string text)
    {
        return Encoding.UTF8.GetBytes(text);
    }
}
