namespace Contactor.Boards;

/// <summary>A model of relay board the app supports, as the board catalogue lists it.</summary>
/// <param name="Id">The value of <c>Board:Type</c> that chooses it, e.g. <c>vm201</c>. Lower case.</param>
/// <param name="Name">Its name as people know it, e.g. "Velleman VM201".</param>
/// <param name="Capabilities">How many relays and inputs it has, and whether it reports changes by itself.</param>
/// <param name="DefaultPort">The TCP port it listens on unless someone changed it.</param>
/// <param name="LoginMaxLength">The longest user name and password it takes; 0 when it has no login.</param>
/// <param name="LoginHint">
/// What its user name and password may contain, in plain words for the Settings screen, e.g. "At most 8 characters:
/// letters a–z, digits and plain symbols."; null when the length is the only rule.
/// </param>
public sealed record BoardModel(string Id, string Name, BoardCapabilities Capabilities, int DefaultPort, int LoginMaxLength, string? LoginHint = null)
{
    /// <summary>Whether the board can have a login.</summary>
    public bool HasLogin => LoginMaxLength > 0;
}
