namespace Contactor.Boards;

/// <summary>A model of relay board the app supports, as the board catalogue lists it.</summary>
/// <param name="Id">Its id in the board catalogue, saved when the setup guide chooses it, e.g. <c>vm201</c>. Lower case.</param>
/// <param name="Brand">Who makes it, e.g. "Velleman": the setup guide lists the models by brand.</param>
/// <param name="ModelName">Its name without the brand, e.g. "VM201".</param>
/// <param name="Capabilities">How many relays and inputs it has, and whether it reports changes by itself.</param>
/// <param name="DefaultPort">The TCP port it listens on unless someone changed it.</param>
/// <param name="LoginMaxLength">The longest user name and password it takes; 0 when it has no login.</param>
/// <param name="LoginHint">
/// What its user name and password may contain, in plain words for the Settings screen, e.g. "At most 8 characters:
/// letters a–z, digits and plain symbols."; null when the length is the only rule.
/// </param>
/// <param name="PasswordOnly">
/// Whether its login is a password alone, without a user name: the screens then ask for the password only.
/// </param>
public sealed record BoardModel(
    string Id,
    string Brand,
    string ModelName,
    BoardCapabilities Capabilities,
    int DefaultPort,
    int LoginMaxLength,
    string? LoginHint = null,
    bool PasswordOnly = false)
{
    /// <summary>Its name as people know it, with the brand, e.g. "Velleman VM201".</summary>
    public string Name => $"{Brand} {ModelName}";

    /// <summary>Whether the board can have a login.</summary>
    public bool HasLogin => LoginMaxLength > 0;
}
