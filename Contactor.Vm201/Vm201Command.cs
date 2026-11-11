namespace Contactor.Vm201;

/// <summary>
/// Command bytes of the VM201 TCP protocol (port 9760).
/// </summary>
public static class Vm201Command
{
    /// <summary>Board to client: login required.</summary>
    public const byte Auth = (byte)'A';

    /// <summary>Client to board: 9 bytes user name, zero padded.</summary>
    public const byte Username = (byte)'U';

    /// <summary>Client to board: 9 bytes password, zero padded.</summary>
    public const byte Password = (byte)'W';

    /// <summary>Board to client: logged in.</summary>
    public const byte LoggedIn = (byte)'L';

    /// <summary>Board to client: wrong login, followed by <see cref="Closed"/>.</summary>
    public const byte AccessDenied = (byte)'X';

    /// <summary>Both directions: close the session.</summary>
    public const byte Closed = (byte)'C';

    /// <summary>Board to client: 1 byte channel (1–9, 9 = input) and 16 bytes name.</summary>
    public const byte Name = (byte)'N';

    /// <summary>Client to board: request status.</summary>
    public const byte StatusRequest = (byte)'R';

    /// <summary>Board to client: outputs, board timers and input.</summary>
    public const byte Status = (byte)'S';

    /// <summary>Client to board: channel mask, 1 = turn on.</summary>
    public const byte On = (byte)'O';

    /// <summary>Client to board: channel mask, 1 = turn off.</summary>
    public const byte Off = (byte)'F';

    /// <summary>Client to board: channel mask, 1 = toggle.</summary>
    public const byte Toggle = (byte)'T';

    /// <summary>Client to board: channel mask, time 1–99, unit 's', 'm' or 'h'.</summary>
    public const byte Pulse = (byte)'P';

    /// <summary>Client to board: set all outputs, 1 = on, 0 = off.</summary>
    public const byte Update = (byte)'V';

    /// <summary>Client to board: enable the board's own timer programs (mask).</summary>
    public const byte TimerEnable = (byte)'E';

    /// <summary>Client to board: disable the board's own timer programs (mask).</summary>
    public const byte TimerDisable = (byte)'D';

    /// <summary>Client to board: toggle the board's own timer programs (mask).</summary>
    public const byte TimerToggle = (byte)'G';

    /// <summary>Number of data bytes in a <see cref="Status"/> packet.</summary>
    public const int StatusDataLength = 3;

    /// <summary>Number of data bytes in a <see cref="Username"/> or <see cref="Password"/> packet.</summary>
    public const int CredentialDataLength = 9;

    /// <summary>Maximum length of a user name or password.</summary>
    public const int CredentialMaxLength = 8;

    /// <summary>Number of data bytes in a <see cref="Name"/> packet.</summary>
    public const int NameDataLength = 17;

    /// <summary>The longest packet the protocol defines (<see cref="Name"/>).</summary>
    public const int MaxPacketLength = Vm201PacketCodec.FramingLength + NameDataLength;

    /// <summary>
    /// The total packet length (LEN) the protocol defines for a command, or null for an unknown command.
    /// </summary>
    public static int? ExpectedPacketLength(byte command)
    {
        return command switch
        {
            Auth or LoggedIn or AccessDenied or Closed or StatusRequest => 5,
            On or Off or Toggle or Update or TimerEnable or TimerDisable or TimerToggle => 6,
            Status or Pulse => 8,
            Username or Password => 14,
            Name => 22,
            _ => null,
        };
    }
}
