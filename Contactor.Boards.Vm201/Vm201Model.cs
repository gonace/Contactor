namespace Contactor.Boards.Vm201;

/// <summary>The Velleman VM201 in the board catalogue.</summary>
public static class Vm201Model
{
    /// <summary>The port a VM201 listens on unless someone changed it.</summary>
    public const int DefaultPort = 9760;

    public static BoardModel Model { get; } = new(
        "vm201",
        "Velleman VM201",
        Vm201Board.Vm201Capabilities,
        DefaultPort,
        Vm201Command.CredentialMaxLength,
        $"At most {Vm201Command.CredentialMaxLength} characters: letters a–z, digits and plain symbols.");
}
