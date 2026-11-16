namespace Contactor.Boards.Velleman;

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
        new LoginHint(LoginHintKind.ListedSymbols, Vm204Api.CredentialMaxLength, Vm204Api.CredentialsTotalMaxLength, Vm204Login.SymbolList));
}
