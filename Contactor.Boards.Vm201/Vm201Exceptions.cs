namespace Contactor.Boards.Vm201;

/// <summary>
/// The board rejected the user name or password.
/// </summary>
public sealed class Vm201AccessDeniedException : Exception
{
    public Vm201AccessDeniedException()
        : base("The VM201 board rejected the user name or password.")
    {
    }

    public Vm201AccessDeniedException(string message)
        : base(message)
    {
    }

    public Vm201AccessDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
