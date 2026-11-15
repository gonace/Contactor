namespace Contactor.Boards.DAEnetIP2v2;

public sealed record DAEnetIP2v2Reading(ulong Relays);
public sealed record DAEnetIP2v2State(bool IsConnected, DAEnetIP2v2Reading? Reading, DateTimeOffset? LastContactUtc)
{
    public static DAEnetIP2v2State Initial { get; } = new(false, null, null);
    public ulong Relays => Reading?.Relays ?? 0;
}
