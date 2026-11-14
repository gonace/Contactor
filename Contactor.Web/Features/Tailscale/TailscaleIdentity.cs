using System.Globalization;
using System.Text;
using Contactor.Web.Features.Identity;
using Microsoft.Extensions.Options;

namespace Contactor.Web.Features.Tailscale;

/// <summary>
/// Tailscale identity as the sign-in: the person is who Tailscale Serve says (<c>Tailscale-User-Login</c>). Serve removes
/// any copy of that header a client sends itself, and sends none for tagged devices or Funnel. The header is believed
/// only on a request that came in on the app's address for Serve (<see cref="TailscaleOptions.ServeAddress"/>), which
/// only the container itself can reach. Not on 127.0.0.1: tailscaled passes connections from the Tailscale network
/// there, past Serve, and so could anyone on that network with a header of their own.
/// </summary>
public sealed class TailscaleIdentity : ISignInMethod
{
    /// <summary>The header Tailscale Serve puts the person's Tailscale login in.</summary>
    public const string HeaderName = "Tailscale-User-Login";

    private readonly TailscaleOptions _options;

    public TailscaleIdentity(IOptions<TailscaleOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public Task<string?> AuthenticateAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!CameThroughServe(context)
            || context.Request.Headers[HeaderName] is not [{ } value]
            || Decode(value) is not { } login
            || !IdentityRules.IsValidEmail(login))
        {
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult<string?>(IdentityRules.NormalizeEmail(login));
    }

    // Whether the request came in on the app's address for Serve: the connection's own end, not the client's.
    private bool CameThroughServe(HttpContext context)
    {
        return _options.ServeEndPoint is { } serve
            && context.Connection.LocalPort == serve.Port
            && context.Connection.LocalIpAddress is { } local
            && (local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : local).Equals(serve.Address);
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
            // Tailscale encodes only what isn't plain ASCII: anything else in encoded words isn't from Tailscale.
            return decoded.Length == 0 || decoded.Any(char.IsControl) || decoded.All(char.IsAscii) ? null : decoded;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
