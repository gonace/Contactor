namespace Contactor.Boards;

/// <summary>Why what someone typed isn't a board's address; the app puts it in words, in its own language.</summary>
public enum BoardEndpointError
{
    /// <summary>Nothing was typed.</summary>
    NoHost,

    /// <summary>What was typed isn't a host name or an IP address.</summary>
    NotAnAddress,

    /// <summary>The port isn't from 1 to 65535.</summary>
    BadPort,
}

/// <summary>Where a board is on the network: a host name or IP address and a TCP port.</summary>
public sealed record BoardEndpoint(string Host, int Port)
{
    // A DNS name can't be longer; anything longer isn't an address.
    private const int MaxHostLength = 253;

    /// <summary>
    /// Whether there is an address at all. A new installation has none until someone enters it, and nothing connects
    /// before.
    /// </summary>
    public bool HasHost => !string.IsNullOrWhiteSpace(Host);

    /// <summary>An endpoint from what someone typed, or null with the reason when it can't be one.</summary>
    public static BoardEndpoint? TryCreate(string? host, int port, out BoardEndpointError? error)
    {
        host = host?.Trim() ?? "";
        if (host.Length == 0)
        {
            error = BoardEndpointError.NoHost;
            return null;
        }

        if (host.Length > MaxHostLength || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            error = BoardEndpointError.NotAnAddress;
            return null;
        }

        if (port is < 1 or > 65535)
        {
            error = BoardEndpointError.BadPort;
            return null;
        }

        error = null;
        return new BoardEndpoint(host, port);
    }

    public override string ToString()
    {
        return Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
    }
}
