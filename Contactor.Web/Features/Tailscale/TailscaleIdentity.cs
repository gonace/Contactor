using System.Globalization;
using System.Net;
using System.Text;
using Contactor.Web.Features.Identity;

namespace Contactor.Web.Features.Tailscale;

/// <summary>
/// Tailscale identity as the sign-in: the person is who Tailscale Serve says (<c>Tailscale-User-Login</c>). Serve removes
/// any copy of that header a client sends itself, and sends none for tagged devices or Funnel. The header is believed
/// only on a request from the machine itself, where Serve connects from: anyone who reaches the app's port another way
/// could write it. Nothing in the app rewrites the remote address (no forwarded headers).
/// </summary>
public sealed class TailscaleIdentity : ISignInMethod
{
    /// <summary>The header Tailscale Serve puts the person's Tailscale login in.</summary>
    public const string HeaderName = "Tailscale-User-Login";

    public Task<string?> AuthenticateAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsFromThisMachine(context.Connection.RemoteIpAddress)
            || context.Request.Headers[HeaderName] is not [{ } value]
            || Decode(value) is not { } login
            || !IdentityRules.IsValidEmail(login))
        {
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult<string?>(IdentityRules.NormalizeEmail(login));
    }

    private static bool IsFromThisMachine(IPAddress? address)
    {
        return address is not null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    }

    /// <summary>
    /// The header's value as Tailscale wrote it: plain ASCII as it is, anything else as RFC 2047 encoded words
    /// (<c>=?utf-8?q?...?=</c>, separated by spaces). Null when it is neither.
    /// </summary>
    public static string? Decode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value = value.Trim();
        if (value.Length == 0 || value.Any(c => c > '~' || char.IsControl(c)))
        {
            return null;
        }

        if (!value.StartsWith("=?", StringComparison.Ordinal))
        {
            return value;
        }

        // Encoded words next to each other, with only spaces between: the spaces aren't part of the text.
        var bytes = new List<byte>();
        foreach (var word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            const string Prefix = "=?utf-8?q?";
            if (word.Length < Prefix.Length + 2
                || !word.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                || !word.EndsWith("?=", StringComparison.Ordinal))
            {
                return null;
            }

            var text = word[Prefix.Length..^2];
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '_')
                {
                    bytes.Add((byte)' ');
                }
                else if (text[i] == '=')
                {
                    if (i + 2 >= text.Length
                        || !byte.TryParse(text.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b))
                    {
                        return null;
                    }

                    bytes.Add(b);
                    i += 2;
                }
                else if (text[i] == '?')
                {
                    return null;
                }
                else
                {
                    bytes.Add((byte)text[i]);
                }
            }
        }

        try
        {
            var decoded = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString([.. bytes]);
            return decoded.Length == 0 || decoded.Any(char.IsControl) ? null : decoded;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
