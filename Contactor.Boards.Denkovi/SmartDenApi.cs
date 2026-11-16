namespace Contactor.Boards.Denkovi;

/// <summary>
/// The Denkovi smartDEN IP-16R-MT as it speaks to the app: Modbus TCP, each frame a 7-byte MBAP header (transaction id,
/// protocol id 0, length, unit id) and the Modbus PDU (function code and data), on one TCP connection. Source: Denkovi's
/// "smartDEN IP-16R-XX User Manual" of 28 September 2020, section 10.2. The board never sends anything by itself: the
/// app polls it. Addresses are the 0-based ones in the frames. See README.md for what is still to check on a real board.
/// </summary>
public static class SmartDenApi
{
    /// <summary>The Modbus TCP port as the board ships ("Modbus-TCP Port", 502).</summary>
    public const int DefaultPort = 502;

    /// <summary>The unit id the manual's examples use; a Modbus TCP server that bridges nothing answers it.</summary>
    public const byte UnitId = 0x00;

    /// <summary>The protocol id of every Modbus TCP frame.</summary>
    public const ushort ProtocolId = 0;

    /// <summary>The MBAP header: transaction id, protocol id, length (2 bytes each) and unit id.</summary>
    public const int HeaderBytes = 7;

    /// <summary>The longest Modbus PDU (function code and data): 253 bytes.</summary>
    public const int MaxPduBytes = 253;

    /// <summary>The largest value of the header's length field: the unit id and the longest PDU.</summary>
    public const int MaxLength = 1 + MaxPduBytes;

    /// <summary>The longest Modbus TCP frame there is; nothing the app reads is longer.</summary>
    public const int MaxFrameBytes = HeaderBytes - 1 + MaxLength;

    /// <summary>How many relays the board has: coils 0 to 15.</summary>
    public const int RelayCount = 16;

    /// <summary>FC 01: read the relays, bit 0 = relay 1.</summary>
    public const byte ReadCoils = 0x01;

    /// <summary>FC 03: read holding registers (configuration). The app doesn't use it.</summary>
    public const byte ReadHoldingRegisters = 0x03;

    /// <summary>FC 05: switch one relay, <see cref="CoilOn"/> or <see cref="CoilOff"/>.</summary>
    public const byte WriteSingleCoil = 0x05;

    /// <summary>FC 06: write one holding register; at 0x0000 to 0x000F a pulse (<see cref="PulseRegister"/>).</summary>
    public const byte WriteSingleRegister = 0x06;

    /// <summary>FC 0F: write several relays. The app switches one relay at a time; the simulator takes it as the board does.</summary>
    public const byte WriteMultipleCoils = 0x0F;

    /// <summary>FC 10: write several holding registers (configuration). The app doesn't use it.</summary>
    public const byte WriteMultipleRegisters = 0x10;

    /// <summary>Set in the function code of an answer that refuses the request; an exception code follows.</summary>
    public const byte ExceptionFlag = 0x80;

    public const ushort CoilOn = 0xFF00;
    public const ushort CoilOff = 0x0000;

    /// <summary>Flips the relay. The app never sends it; the simulator takes it as the manual says the board does.</summary>
    public const ushort CoilToggle = 0xFF02;

    /// <summary>
    /// "Generate pulses on relays 1..16" with FC 06: relay n's register is this + n − 1, and the value is the pulse's
    /// length in <see cref="PulseUnit"/>s, 1 to <see cref="MaxPulseTime"/>. Write only.
    /// </summary>
    public const ushort PulseRegister = 0x0000;

    /// <summary>The longest pulse: 65535 × 100 ms, 6553.5 s.</summary>
    public const ushort MaxPulseTime = ushort.MaxValue;

    /// <summary>The unit of a pulse's length: 100 ms ("milliseconds x 100").</summary>
    public static readonly TimeSpan PulseUnit = TimeSpan.FromMilliseconds(100);

    /// <summary>Modbus exception code 01: the board doesn't know the function.</summary>
    public const byte IllegalFunction = 0x01;

    /// <summary>Modbus exception code 02: an address the board doesn't have.</summary>
    public const byte IllegalDataAddress = 0x02;

    /// <summary>Modbus exception code 03: a value or quantity the board doesn't take.</summary>
    public const byte IllegalDataValue = 0x03;

    /// <summary>Modbus exception code 04: the board failed to do it.</summary>
    public const byte DeviceFailure = 0x04;
}
