namespace Contactor.Boards.Advantech;

public sealed record Adam6000Reading(ulong Relays, ulong Inputs);

public sealed record Adam6000State(bool IsConnected, Adam6000Reading? Reading, DateTimeOffset? LastContactUtc)
{
    public static Adam6000State Initial { get; } = new(false, null, null);
    public ulong Relays => Reading?.Relays ?? 0;
    public ulong Inputs => Reading?.Inputs ?? 0;
}
