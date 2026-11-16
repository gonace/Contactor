namespace Contactor.Boards;

/// <summary>
/// What a command run by <see cref="BoardPoller{TReading}.RunSwitchesAsync"/> hands back: the board's state, read right
/// after the command, or <see cref="Unread"/> when the command went out but that read failed. Only a reading counts as
/// an answered poll.
/// </summary>
public readonly record struct CommandReading<TReading>
{
    private CommandReading(bool isRead, TReading? reading)
    {
        IsRead = isRead;
        Reading = reading;
    }

    /// <summary>
    /// The command went out, but the board's state after it couldn't be read. The poller keeps the last state, doesn't
    /// count it as contact with the board, and reads the board at once.
    /// </summary>
    public static CommandReading<TReading> Unread => default;

    /// <summary>Whether the board's state was read after the command.</summary>
    public bool IsRead { get; }

    /// <summary>The board's state after the command; only when <see cref="IsRead"/>.</summary>
    public TReading? Reading { get; }

    /// <summary>The board's state, read right after the command.</summary>
    public static CommandReading<TReading> Read(TReading reading)
    {
        return new CommandReading<TReading>(true, reading);
    }
}
