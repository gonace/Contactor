namespace Contactor.Boards.Vm204;

/// <summary>The Velleman VM204 in the board catalogue.</summary>
public static class Vm204Model
{
    public static BoardModel Model { get; } = new(
        "vm204",
        "Velleman",
        "VM204",
        Vm204Board.Vm204Capabilities,
        Vm204Api.DefaultPort,
        Vm204Api.CredentialMaxLength,
        $"At most {Vm204Api.CredentialMaxLength} characters, {Vm204Api.CredentialsTotalMaxLength} for both together: letters a–z, digits and {Vm204Login.SymbolList}.");
}
