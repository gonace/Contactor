namespace Contactor.Boards.Advantech;

/// <summary>
/// The Advantech ADAM modules as they speak to the app: Modbus TCP, each frame a 7-byte MBAP header (transaction id,
/// protocol id 0, length, unit id) and the Modbus PDU (function code and data), on one TCP connection. Sources: the
/// "ADAM-6000 Series User Manual", Edition 12 (November 2022), section 7.3 and appendix B, and for the ADAM-6260 the
/// "ADAM-6200 Series User Manual", Edition 5 (July 2021), appendix A. The module never sends anything by itself: the
/// app polls it. See README.md for what the manuals leave open, above all which address is in the frame for a manual
/// address such as 00017.
/// </summary>
public static class AdamApi
{
    /// <summary>
    /// The Modbus TCP port. The ADAM-6000 manual names none; the ADAM-6200 manual lists "TCP Modbus TCP 502", and 502 is
    /// Modbus TCP's own port.
    /// </summary>
    public const int DefaultPort = 502;

    /// <summary>
    /// The unit id: "unit identifier - 1 (ADAM-6000 always 1)" (ADAM-6000 manual, figure B.1); the ADAM-6200 manual gives
    /// the module's address as "Always 01" (appendix B).
    /// </summary>
    public const byte UnitId = 0x01;

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

    /// <summary>
    /// The frame address of the first relay, the manual's coil 00017 ("DO value", channel 0). Modbus numbers a coil
    /// from 1 and addresses it in the frame from 0, so coil 00017 is address 16 (0x0010). The manual's own examples
    /// don't agree on this (see README.md); a real module must show it.
    /// </summary>
    public const ushort RelayAddress = 16;

    /// <summary>The frame address of the first digital input, the manual's 00001 ("DI value", channel 0), as <see cref="RelayAddress"/>.</summary>
    public const ushort InputAddress = 0;

    /// <summary>FC 01 "Read coil status": read the relays, bit 0 = relay 1.</summary>
    public const byte ReadCoils = 0x01;

    /// <summary>FC 02 "Read input status": read the digital inputs, bit 0 = input 1.</summary>
    public const byte ReadDiscreteInputs = 0x02;

    /// <summary>FC 05 "Force single coil": switch one relay, <see cref="CoilOn"/> or <see cref="CoilOff"/>.</summary>
    public const byte WriteSingleCoil = 0x05;

    /// <summary>FC 0F "Force multiple coils". The app switches one relay at a time; the simulator takes it as the manual says.</summary>
    public const byte WriteMultipleCoils = 0x0F;

    /// <summary>Set in the function code of an answer that refuses the request; an exception code follows.</summary>
    public const byte ExceptionFlag = 0x80;

    /// <summary>"A value of FF 00 (hex) requests it to be ON."</summary>
    public const ushort CoilOn = 0xFF00;

    /// <summary>"A value of 00 00 (hex) requests it to be OFF."</summary>
    public const ushort CoilOff = 0x0000;

    /// <summary>Modbus exception code 01: the module doesn't know the function.</summary>
    public const byte IllegalFunction = 0x01;

    /// <summary>Modbus exception code 02: an address the module doesn't have.</summary>
    public const byte IllegalDataAddress = 0x02;

    /// <summary>Modbus exception code 03: a value or quantity the module doesn't take.</summary>
    public const byte IllegalDataValue = 0x03;

    /// <summary>Modbus exception code 04: the module failed to do it.</summary>
    public const byte DeviceFailure = 0x04;
}
