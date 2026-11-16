namespace Contactor.Boards;

/// <summary>
/// What a command run by <see cref="BoardPoller{TReading}.RunSwitchesAsync"/> hands back: the board's state, read right
/// after the command, or none (the default value) when the command went out but that read failed. Only a reading counts
/// as an answered poll; without one the poller keeps the last state, doesn't count it as contact with the board, and
/// reads the board at once.
/// </summary>
public readonly record struct CommandReading<TReading>
{
    /// <summary>The board's state, read right after the command.</summary>
    public CommandReading(TReading reading)
    {
        IsRead = true;
        Reading = reading;
    }

    /// <summary>Whether the board's state was read after the command; false for the default value.</summary>
    public bool IsRead { get; }

    /// <summary>The board's state after the command; only when <see cref="IsRead"/>.</summary>
    public TReading? Reading { get; }
}
