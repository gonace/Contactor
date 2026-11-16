namespace Contactor.Boards.Velleman;

/// <summary>The board refused the user name and password, or the key from an earlier login.</summary>
public sealed class Vm204AccessDeniedException : Exception, IConnectionProblemSource
{
    public Vm204AccessDeniedException()
        : base("The VM204 refused the user name or password.")
    {
    }

    public Vm204AccessDeniedException(string message)
        : base(message)
    {
    }

    public Vm204AccessDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <param name="message">What is wrong, in English, without the login.</param>
    /// <param name="problem">
    /// A login needed or refused, for the screens; null when the board refused only the key from an earlier login,
    /// which the app gets again by logging in.
    /// </param>
    public Vm204AccessDeniedException(string message, ConnectionProblem? problem)
        : base(message)
    {
        Problem = problem;
    }

    /// <summary>A login needed or refused; null when the board refused only the key, or when not given.</summary>
    public ConnectionProblem? Problem { get; }
}

/// <summary>The board answered something a VM204 doesn't: another status code, too big, or not the JSON it sends.</summary>
public sealed class Vm204ProtocolException : Exception
{
    public Vm204ProtocolException()
        : base("The VM204's answer wasn't what a VM204 sends.")
    {
    }

    public Vm204ProtocolException(string message)
        : base(message)
    {
    }

    public Vm204ProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
