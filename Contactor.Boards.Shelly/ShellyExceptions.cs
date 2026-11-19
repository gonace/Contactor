namespace Contactor.Boards.Shelly;

/// <summary>
/// The device answered something a Shelly doesn't: another status code, too big, not a response frame, or a frame
/// for another request.
/// </summary>
public sealed class ShellyProtocolException : Exception
{
    public ShellyProtocolException()
        : base("The device's answer wasn't what a Shelly sends.")
    {
    }

    public ShellyProtocolException(string message)
        : base(message)
    {
    }

    public ShellyProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The device asks for a password and the app has none, or it refused the password.</summary>
public sealed class ShellyAccessDeniedException : Exception, IConnectionProblemSource
{
    public ShellyAccessDeniedException()
        : base("The Shelly refused the password.")
    {
    }

    public ShellyAccessDeniedException(string message)
        : base(message)
    {
    }

    public ShellyAccessDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <param name="message">What is wrong, in English, without the password.</param>
    /// <param name="problem">Which of the two it is, for the screens.</param>
    public ShellyAccessDeniedException(string message, ConnectionProblem problem)
        : base(message)
    {
        Problem = problem;
    }

    /// <summary>A password needed or refused; null when not given.</summary>
    public ConnectionProblem? Problem { get; }
}

/// <summary>Why the device at the address isn't one the app can use as the chosen entry.</summary>
public enum ShellyMismatch
{
    /// <summary>Not a Shelly Gen2+: no <c>/rpc</c>, or no <c>gen</c> of 2 or more, e.g. a Gen1 Shelly.</summary>
    NotGen2,

    /// <summary>A device in the cover profile: its outputs drive a cover, not relays.</summary>
    CoverProfile,

    /// <summary>Firmware before <see cref="ShellyApi.MinFirmware"/>, or a version the app can't read.</summary>
    OldFirmware,

    /// <summary>Another number of switches than the chosen entry has.</summary>
    RelayCount,
}

/// <summary>The device at the address isn't one the app can use as the chosen entry: the app switches nothing on it.</summary>
public sealed class ShellyWrongDeviceException : Exception, IConnectionProblemSource
{
    public ShellyWrongDeviceException()
        : base("The device at this address isn't a Shelly the app can use.")
    {
    }

    public ShellyWrongDeviceException(string message)
        : base(message)
    {
    }

    public ShellyWrongDeviceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <param name="message">What is wrong, in plain English.</param>
    /// <param name="mismatch">Which of the checks it failed.</param>
    /// <param name="problem">
    /// For the screens: another model or another number of relays (with the entry for its switch count when it has one),
    /// the cover profile, or firmware too old.
    /// </param>
    public ShellyWrongDeviceException(string message, ShellyMismatch mismatch, ConnectionProblem? problem)
        : base(message)
    {
        Mismatch = mismatch;
        Problem = problem;
    }

    /// <summary>Which of the checks it failed.</summary>
    public ShellyMismatch Mismatch { get; }

    /// <summary>Another model or number of relays, the cover profile, or firmware too old; null when not given.</summary>
    public ConnectionProblem? Problem { get; }
}

/// <summary>
/// The device answered 429 Too Many Requests: after too many wrong passwords, or with too many clients at once. The
/// client never tries again by itself within a request; the poller's next attempt comes after its wait.
/// </summary>
public sealed class ShellyThrottledException : Exception, IConnectionProblemSource
{
    public ShellyThrottledException()
        : base("The Shelly asks to wait: it refuses requests for a while after too many wrong passwords or too many clients. The app tries again by itself.")
    {
    }

    public ShellyThrottledException(string message)
        : base(message)
    {
    }

    public ShellyThrottledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <param name="problem">For the screens: asked to wait; null for none.</param>
    public ShellyThrottledException(ConnectionProblem? problem)
        : this()
    {
        Problem = problem;
    }

    /// <summary>
    /// <see cref="ConnectionProblemKind.AskedToWait"/>: the screens say to wait, and to check a password just changed.
    /// Null when not given.
    /// </summary>
    public ConnectionProblem? Problem { get; }
}

/// <summary>The device answered a request with an error frame, e.g. -109 "Overtemp condition present".</summary>
public sealed class ShellyRpcException : Exception
{
    public ShellyRpcException()
        : base("The Shelly refused the request.")
    {
    }

    public ShellyRpcException(string message)
        : base(message)
    {
    }

    public ShellyRpcException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <param name="error">The device's error, its own message included.</param>
    public ShellyRpcException(ShellyRpcError error)
        : base(error?.ToString())
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    /// <summary>The device's error; null when not given.</summary>
    public ShellyRpcError? Error { get; }
}
