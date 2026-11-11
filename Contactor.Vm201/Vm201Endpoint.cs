namespace Contactor.Vm201;

/// <summary>Where the board is: a host name or IP address and a TCP port (9760 on a VM201).</summary>
public sealed record Vm201Endpoint(string Host, int Port)
{
    public const int DefaultPort = 9760;

    // A DNS name can't be longer; anything longer isn't an address.
    private const int MaxHostLength = 253;

    /// <summary>An endpoint from what someone typed, or null with the reason (in Swedish) when it can't be one.</summary>
    public static Vm201Endpoint? TryCreate(string? host, int port, out string? error)
    {
        host = host?.Trim() ?? "";
        if (host.Length == 0)
        {
            error = "Skriv reläkortets adress, t.ex. 192.168.1.50.";
            return null;
        }

        if (host.Length > MaxHostLength || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            error = $"{host} är ingen adress. Skriv t.ex. 192.168.1.50.";
            return null;
        }

        if (port is < 1 or > 65535)
        {
            error = "Porten ska vara ett tal mellan 1 och 65535 (oftast 9760).";
            return null;
        }

        error = null;
        return new Vm201Endpoint(host, port);
    }

    public override string ToString()
    {
        return Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
    }
}
