using System.Net;

namespace Contactor.Web.Features.Access;

/// <summary>
/// Requests from the network the app runs on, the only ones "Only on this network" and setup mode answer: a machine
/// with a public address, such as a cloud server, must not put the app on the internet directly.
/// </summary>
public static class ThisNetwork
{
    /// <summary>
    /// The English of the plain text a request from outside gets, with 403; the app says it in its own language
    /// (Setup.Refused.OwnNetwork).
    /// </summary>
    public const string Refusal = "Contactor can only be opened on its own network.";

    /// <summary>
    /// Whether requests must come from this network: with "Only on this network" as the way in, and while no sign-in
    /// is chosen (setup mode). Other ways in reach the app from inside its container, and their sign-in decides.
    /// </summary>
    public static bool Required(AccessMethod? wayIn, bool signInChosen)
    {
        return !signInChosen || wayIn?.Id == AccessCatalogue.LocalId;
    }

    /// <summary>
    /// Whether the request came from this network. Only the connection's own address counts: forwarded headers are
    /// never believed. A request without an address is refused, as <see cref="ThisMachine"/> refuses it.
    /// </summary>
    public static bool Asked(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Connection.RemoteIpAddress is { } remote && Contains(remote);
    }

    /// <summary>
    /// Whether the address is on a local network: 10/8, 172.16/12 and 192.168/16 (RFC 1918), loopback (127/8, ::1),
    /// link-local (169.254/16, fe80::/10) and IPv6 unique local addresses (fc00::/7). An IPv4 address that Kestrel gives
    /// as IPv6 (::ffff:a.b.c.d) counts as that IPv4 address. Nothing else, not even carrier NAT (100.64/10).
    /// </summary>
    public static bool Contains(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] == 10
                || (bytes[0] == 172 && (bytes[1] & 0xF0) == 16)
                || (bytes[0] == 192 && bytes[1] == 168)
                || bytes[0] == 127
                || (bytes[0] == 169 && bytes[1] == 254);
        }

        return bytes.Length == 16
            && ((bytes.AsSpan(0, 15).IndexOfAnyExcept((byte)0) < 0 && bytes[15] == 1)
                || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
                || (bytes[0] & 0xFE) == 0xFC);
    }
}
