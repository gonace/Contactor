using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Contactor.Boards.Shelly;

/// <summary>A digest challenge from a 401's <c>WWW-Authenticate</c> header ("Authentication").</summary>
/// <param name="Realm">"device_id of the Shelly device".</param>
/// <param name="Nonce">A base64 token from firmware 2.0.0, a number before.</param>
/// <param name="Stale">"the nonce has expired but credentials are valid" (firmware 2.0.0 and later).</param>
public sealed record ShellyChallenge(string Realm, string Nonce, bool Stale);

/// <summary>The parameters of an <c>Authorization: Digest …</c> header, as the simulator reads them.</summary>
public sealed record ShellyCredentials(string Username, string Realm, string Nonce, string Uri, string Algorithm, string Qop, string NonceCount, string ClientNonce, string Response);

/// <summary>
/// HTTP digest authentication with SHA-256 as RFC 7616 defines it and the Shelly uses it ("Authentication"): user
/// "admin", realm the device id, <c>qop=auth</c>. <c>HA1 = SHA256(admin:realm:password)</c>,
/// <c>HA2 = SHA256(POST:/rpc)</c>, <c>response = SHA256(HA1:nonce:nc:cnonce:auth:HA2)</c>, each as lower-case hex. Every
/// header the device sends is read with a length limit, and a realm or nonce with a quote, a backslash or a control
/// character is refused, so it can't break the header the app sends back.
/// </summary>
public static class ShellyDigest
{
    /// <summary>The only algorithm the Shelly uses: "algorithm: string, SHA-256".</summary>
    public const string Algorithm = "SHA-256";

    /// <summary>The method of every request the app sends.</summary>
    public const string Method = "POST";

    /// <summary>The longest header the app reads; the docs' challenge is about 150 characters.</summary>
    public const int MaxHeaderLength = 2048;

    // A realm or nonce longer than this isn't one a Shelly sends.
    private const int MaxValueLength = 256;

    /// <summary>
    /// Reads a challenge from the <c>WWW-Authenticate</c> headers of a 401, e.g.
    /// <c>Digest qop="auth", realm="shellypro4pm-f008d1d8b8b8", nonce="60dc59c6", algorithm=SHA-256</c>. Takes the first
    /// Digest challenge with SHA-256 and qop "auth"; null when there is none the app can answer.
    /// </summary>
    public static ShellyChallenge? ParseChallenge(IEnumerable<string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        foreach (var header in headers.Take(8))
        {
            if (ParseScheme(header) is not { } parameters)
            {
                continue;
            }

            if (!parameters.TryGetValue("algorithm", out var algorithm) || !algorithm.Equals(Algorithm, StringComparison.OrdinalIgnoreCase)
                || !parameters.TryGetValue("qop", out var qop) || !qop.Split(',').Any(q => q.Trim().Equals("auth", StringComparison.OrdinalIgnoreCase))
                || !parameters.TryGetValue("realm", out var realm) || !IsPlain(realm)
                || !parameters.TryGetValue("nonce", out var nonce) || !IsPlain(nonce))
            {
                continue;
            }

            var stale = parameters.TryGetValue("stale", out var value) && value.Equals("true", StringComparison.OrdinalIgnoreCase);
            return new ShellyChallenge(realm, nonce, stale);
        }

        return null;
    }

    /// <summary>The <c>Authorization</c> header's value for one request with this challenge.</summary>
    /// <param name="challenge">The device's challenge.</param>
    /// <param name="password">The device's password. A secret: never log the header.</param>
    /// <param name="nonceCount">The nonce count, from 1: "00000001" on the wire.</param>
    /// <param name="clientNonce">A random client nonce.</param>
    /// <param name="uri">The request's path.</param>
    public static string Authorization(ShellyChallenge challenge, string password, int nonceCount, string clientNonce, string uri = ShellyApi.RpcPath)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(clientNonce);
        ArgumentOutOfRangeException.ThrowIfLessThan(nonceCount, 1);
        if (!IsPlain(challenge.Realm) || !IsPlain(challenge.Nonce) || !IsPlain(clientNonce))
        {
            throw new ArgumentException("A realm, nonce or client nonce with a quote, a backslash or a control character can't be sent.", nameof(challenge));
        }

        var nc = NonceCount(nonceCount);
        var response = Response(ShellyApi.Username, challenge.Realm, password, Method, uri, challenge.Nonce, nc, clientNonce);
        return $"Digest username=\"{ShellyApi.Username}\", realm=\"{challenge.Realm}\", nonce=\"{challenge.Nonce}\", uri=\"{uri}\", algorithm={Algorithm}, response=\"{response}\", qop=auth, nc={nc}, cnonce=\"{clientNonce}\"";
    }

    /// <summary>"nc: … 8 hexadecimal digits padded with leading zeros (e.g., "00000001", "0000000a")".</summary>
    public static string NonceCount(int count)
    {
        return count.ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>A random client nonce: a number, as the docs' examples have (313273957).</summary>
    public static string NewClientNonce()
    {
        return RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The digest response (RFC 7616 section 3.4.1, qop "auth"): SHA256(HA1:nonce:nc:cnonce:auth:HA2), with HA1 =
    /// SHA256(user:realm:password) and HA2 = SHA256(method:uri), all lower-case hex.
    /// </summary>
    public static string Response(string username, string realm, string password, string method, string uri, string nonce, string nonceCount, string clientNonce)
    {
        ArgumentNullException.ThrowIfNull(password);
        var ha1 = Hash($"{username}:{realm}:{password}");
        var ha2 = Hash($"{method}:{uri}");
        return Hash($"{ha1}:{nonce}:{nonceCount}:{clientNonce}:auth:{ha2}");
    }

    /// <summary>SHA-256 of the UTF-8 bytes, as lower-case hex.</summary>
    public static string Hash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>A challenge as the device sends it in <c>WWW-Authenticate</c>; for the simulator.</summary>
    public static string Challenge(string realm, string nonce, bool stale = false)
    {
        return $"Digest qop=\"auth\", realm=\"{realm}\", nonce=\"{nonce}\", algorithm={Algorithm}{(stale ? ", stale=true" : "")}";
    }

    /// <summary>Reads an <c>Authorization: Digest …</c> header; null when it isn't one with every part. For the simulator.</summary>
    public static ShellyCredentials? ParseAuthorization(string? header)
    {
        if (ParseScheme(header) is not { } p)
        {
            return null;
        }

        string Get(string name) => p.TryGetValue(name, out var value) ? value : "";
        var credentials = new ShellyCredentials(Get("username"), Get("realm"), Get("nonce"), Get("uri"), Get("algorithm"), Get("qop"), Get("nc"), Get("cnonce"), Get("response"));
        return credentials.Username.Length == 0 || credentials.Nonce.Length == 0 || credentials.Response.Length == 0 ? null : credentials;
    }

    // Whether a value can go between quotes in a header as it is: printable ASCII without a quote or a backslash.
    private static bool IsPlain(string value)
    {
        return value.Length is > 0 and <= MaxValueLength && value.All(c => c is >= ' ' and <= '~' and not '"' and not '\\');
    }

    // "Digest a=b, c="d"" as its parameters, names in lower case; null when it isn't a Digest header or can't be read.
    private static Dictionary<string, string>? ParseScheme(string? header)
    {
        if (header is null || header.Length > MaxHeaderLength)
        {
            return null;
        }

        var text = header.Trim();
        const string scheme = "Digest";
        if (!text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) || text.Length <= scheme.Length || text[scheme.Length] != ' ')
        {
            return null;
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = scheme.Length;
        while (true)
        {
            while (i < text.Length && (text[i] == ' ' || text[i] == ','))
            {
                i++;
            }

            if (i >= text.Length)
            {
                return parameters;
            }

            var nameStart = i;
            while (i < text.Length && text[i] != '=' && text[i] != ',' && text[i] != ' ')
            {
                i++;
            }

            var name = text[nameStart..i].ToLowerInvariant();
            while (i < text.Length && text[i] == ' ')
            {
                i++;
            }

            if (name.Length == 0 || i >= text.Length || text[i] != '=')
            {
                return null;
            }

            i++;
            while (i < text.Length && text[i] == ' ')
            {
                i++;
            }

            string value;
            if (i < text.Length && text[i] == '"')
            {
                var builder = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                    }

                    builder.Append(text[i]);
                    i++;
                }

                if (i >= text.Length)
                {
                    return null;
                }

                i++;
                value = builder.ToString();
            }
            else
            {
                var valueStart = i;
                while (i < text.Length && text[i] != ',' && text[i] != ' ')
                {
                    i++;
                }

                value = text[valueStart..i];
            }

            // The first of a name counts; a header can't grow the dictionary past its own length anyway.
            parameters.TryAdd(name, value);
        }
    }
}
