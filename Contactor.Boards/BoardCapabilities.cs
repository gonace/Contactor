namespace Contactor.Boards;

/// <summary>What a kind of board has and does.</summary>
/// <param name="RelayCount">How many relays, numbered 1 to this.</param>
/// <param name="InputCount">How many digital inputs, numbered 1 to this.</param>
/// <param name="ReportsChanges">
/// Whether the board reports every change by itself, including ones made at the board. Without it the app has to
/// ask, and changes made elsewhere show later.
/// </param>
public sealed record BoardCapabilities(int RelayCount, int InputCount, bool ReportsChanges)
{
    /// <summary>All of the board's relays.</summary>
    public ChannelMask AllRelays => ChannelMask.FirstN(RelayCount);
}
