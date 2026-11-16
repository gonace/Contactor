namespace Contactor.Boards.Advantech;

public sealed record Adam6200Reading(ulong Relays);

public sealed record Adam6200State(bool IsConnected, Adam6200Reading? Reading, DateTimeOffset? LastContactUtc)
{
    public static Adam6200State Initial { get; } = new(false, null, null);
    public ulong Relays => Reading?.Relays ?? 0;
}
