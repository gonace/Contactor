namespace Contactor.Boards;

/// <summary>Where a board is on the network: a host name or IP address and a TCP port.</summary>
public sealed record BoardEndpoint(string Host, int Port)
{
    // A DNS name can't be longer; anything longer isn't an address.
    private const int MaxHostLength = 253;

    /// <summary>An endpoint from what someone typed, or null with the reason when it can't be one.</summary>
    public static BoardEndpoint? TryCreate(string? host, int port, out string? error)
    {
        host = host?.Trim() ?? "";
        if (host.Length == 0)
        {
            error = "Enter the relay board's address, e.g. 192.168.1.50.";
            return null;
        }

        if (host.Length > MaxHostLength || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            error = $"{host} isn't an address. Enter e.g. 192.168.1.50.";
            return null;
        }

        if (port is < 1 or > 65535)
        {
            error = "The port is a number from 1 to 65535.";
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
