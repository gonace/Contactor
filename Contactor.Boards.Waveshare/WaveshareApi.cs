namespace Contactor.Boards.Waveshare;

/// <summary>
/// The Waveshare Modbus PoE ETH relays as their factory setting speaks to the app: "Modbus RTU over TCP", raw Modbus
/// RTU frames (unit id, function, data, CRC-16) on a TCP connection, without the MBAP header of Modbus TCP. Source:
/// Waveshare's wiki (https://www.waveshare.com/wiki/Modbus_POE_ETH_Relay and the (B), 16CH and 30CH pages). The board
/// never sends anything by itself: the app polls it. Addresses are the 0-based ones in the frames. See README.md for
/// what is still to check on a real board.
/// </summary>
public static class WaveshareApi
{
    /// <summary>The port of "Modbus RTU over TCP" as the board ships. To check on a real board.</summary>
    public const int DefaultPort = 4196;

    /// <summary>The board's Modbus unit id (holding register 0x4000, default 1).</summary>
    public const byte UnitId = 0x01;

    /// <summary>FC 01: read the relays, bit 0 = relay 1.</summary>
    public const byte ReadCoils = 0x01;

    /// <summary>FC 02: read the digital inputs ((B) and (C)), bit 0 = input 1. The bit order is to check on a real board.</summary>
    public const byte ReadDiscreteInputs = 0x02;

    /// <summary>FC 03: read holding registers, here the relay control modes.</summary>
    public const byte ReadHoldingRegisters = 0x03;

    /// <summary>FC 05: switch one relay, <see cref="CoilOn"/> or <see cref="CoilOff"/>.</summary>
    public const byte WriteSingleCoil = 0x05;

    /// <summary>FC 06: write one holding register. The simulator takes it; the app writes the modes with FC 10.</summary>
    public const byte WriteSingleRegister = 0x06;

    /// <summary>FC 0F: write several relays. The simulator doesn't take it; the app switches one relay at a time.</summary>
    public const byte WriteMultipleCoils = 0x0F;

    /// <summary>FC 10: write several holding registers, here all relay control modes at once.</summary>
    public const byte WriteMultipleRegisters = 0x10;

    /// <summary>Set in the function code of an answer that refuses the request; an exception code follows.</summary>
    public const byte ExceptionFlag = 0x80;

    public const ushort CoilOn = 0xFF00;
    public const ushort CoilOff = 0x0000;

    /// <summary>Flips the relay. The app never sends it; the simulator takes it as the board does.</summary>
    public const ushort CoilToggle = 0x5500;

    /// <summary>
    /// "Flash on" with FC 05: relay n's address is this + n − 1, and the value is a time in <see cref="FlashUnit"/>s,
    /// 1 to <see cref="MaxFlashTime"/>. The board turns the relay on, and off again by itself after the time, and echoes
    /// the request. From Waveshare's development protocol pages, which disagree a little per model: to check on a real
    /// board.
    /// </summary>
    public const ushort FlashOnAddress = 0x0200;

    /// <summary>How many flash-on addresses there are: the range up to "flash off" at 0x0400.</summary>
    public const int FlashOnAddressCount = 0x0200;

    /// <summary>The longest flash time: 0x7FFF × 100 ms, 3276.7 s.</summary>
    public const ushort MaxFlashTime = 0x7FFF;

    /// <summary>The unit of a flash time: 100 ms.</summary>
    public static readonly TimeSpan FlashUnit = TimeSpan.FromMilliseconds(100);

    /// <summary>The first relay control mode register ((B) and (C)): relay n's is this + n − 1.</summary>
    public const ushort RelayModeRegister = 0x1000;

    /// <summary>How many relay control mode registers there are, one per relay of the (B) and (C).</summary>
    public const int RelayModeCount = 8;

    /// <summary>Relay control mode: the relay is switched by Modbus commands only.</summary>
    public const ushort ModeNormal = 0;

    /// <summary>Relay control mode: the relay follows its input, and commands don't switch it.</summary>
    public const ushort ModeLinkage = 1;

    /// <summary>Relay control mode: each pulse on its input flips the relay.</summary>
    public const ushort ModeToggle = 2;

    /// <summary>Relay control mode: each change of its input's level flips the relay ("jump" on some pages).</summary>
    public const ushort ModeEdge = 3;

    /// <summary>Modbus exception code 01: the board doesn't know the function.</summary>
    public const byte IllegalFunction = 0x01;

    /// <summary>Modbus exception code 02: an address the board doesn't have.</summary>
    public const byte IllegalDataAddress = 0x02;

    /// <summary>Modbus exception code 03: a value or quantity the board doesn't take.</summary>
    public const byte IllegalDataValue = 0x03;

    /// <summary>Modbus exception code 04: the board failed to do it.</summary>
    public const byte DeviceFailure = 0x04;

    /// <summary>The longest Modbus RTU frame there is; nothing the app reads is longer.</summary>
    public const int MaxFrameBytes = 256;
}
