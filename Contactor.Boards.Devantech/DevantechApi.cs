namespace Contactor.Boards.Devantech;

/// <summary>
/// The Devantech ETH relay boards' binary TCP commands, as their technical documents and Devantech's own example code
/// give them: one command byte, its data, and an answer of a fixed length, with no framing and no checksum. All bytes of
/// a command go in one TCP packet. The board never sends anything by itself: the app polls it. See README.md for the
/// sources and what is still to check on a real board.
/// </summary>
public static class DevantechApi
{
    /// <summary>The port of the binary commands as the board ships (0x4456), changeable on its configuration page.</summary>
    public const int DefaultPort = 17494;

    /// <summary>0x10: the module id, hardware version and firmware version, 3 bytes.</summary>
    public const byte GetModuleInfo = 0x10;

    /// <summary>0x20: output active (relay on). Then the relay number (1-based) and the pulse time.</summary>
    public const byte DigitalActive = 0x20;

    /// <summary>0x21: output inactive (relay off). Then the relay number (1-based) and the pulse time.</summary>
    public const byte DigitalInactive = 0x21;

    /// <summary>0x24: the outputs, one bit per output, low byte first.</summary>
    public const byte GetOutputs = 0x24;

    /// <summary>0x25: the digital inputs, high byte first; only models with inputs answer it.</summary>
    public const byte GetInputs = 0x25;

    /// <summary>0x79: the TCP password, its ASCII bytes after the command, with no length and no end byte.</summary>
    public const byte Password = 0x79;

    /// <summary>0x7A: how long the board stays unlocked, 1 byte.</summary>
    public const byte GetUnlockTime = 0x7A;

    /// <summary>The pulse time of 0x20 and 0x21 that keeps the output as set, for a plain ON or OFF.</summary>
    public const byte Permanent = 0x00;

    /// <summary>
    /// The longest pulse time of 0x20: in <see cref="PulseUnit"/>s, 25.5 s. The board turns the relay on, and off again
    /// by itself after the time.
    /// </summary>
    public const byte MaxPulseTime = 0xFF;

    /// <summary>The unit of the pulse time of 0x20 and 0x21: 100 ms.</summary>
    public static readonly TimeSpan PulseUnit = TimeSpan.FromMilliseconds(100);

    /// <summary>The answer to 0x20 and 0x21 when the board did it.</summary>
    public const byte Success = 0x00;

    /// <summary>The answer to 0x20 and 0x21 when the board didn't: e.g. a relay it doesn't have.</summary>
    public const byte Failure = 0x01;

    /// <summary>The answer to 0x79 when the password is right.</summary>
    public const byte PasswordAccepted = 0x01;

    /// <summary>The answer to 0x79 when the password is wrong.</summary>
    public const byte PasswordRefused = 0x02;

    /// <summary>The answer to 0x7A when the board is locked: it needs the password before it switches a relay.</summary>
    public const byte Locked = 0x00;

    /// <summary>The answer to 0x7A when the board has no TCP password.</summary>
    public const byte NoPassword = 0xFF;

    /// <summary>
    /// How long the board stays unlocked without a command: 0x7A answers 1 to this many seconds while unlocked.
    /// </summary>
    public const int UnlockSeconds = 30;

    /// <summary>
    /// The longest password the app sends. The board's documents give no limit; to check on a real board.
    /// </summary>
    public const int PasswordMaxLength = 32;

    /// <summary>
    /// Whether the app sends this character in a password: printable ASCII without the space. The board's documents
    /// only say ASCII; to check on a real board.
    /// </summary>
    public static bool IsPasswordCharacter(char c)
    {
        return c is > ' ' and <= '~';
    }
}
