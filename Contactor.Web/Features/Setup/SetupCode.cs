using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Contactor.Web.Features.Setup;

/// <summary>What became of a setup code someone typed.</summary>
public enum SetupCodeResult
{
    /// <summary>The right code: the browser may go through the setup guide.</summary>
    Accepted,

    /// <summary>Not the code.</summary>
    Wrong,

    /// <summary>Too many wrong codes: no code is tried until <see cref="SetupCode.Wait"/> has passed.</summary>
    Waiting,
}

/// <summary>
/// The setup code: while an installation is being set up (no sign-in chosen), only someone who can read the app's log
/// gets into the setup guide. A new code is made at each start, kept in memory only, and written to the log. Typing it gives the browser a cookie the app has signed, which holds until the code changes.
/// </summary>
public sealed partial class SetupCode
{
    public const string CookieName = "contactor-setup";

    /// <summary>How many wrong codes in a row before the app waits.</summary>
    public const int WrongBeforeWait = 5;

    public static readonly TimeSpan Wait = TimeSpan.FromMinutes(1);

    // No 0/O, 1/I/L, 5/S or 9/G look-alikes: easy to read from a log and type on a phone. 28 characters, 8 of them:
    // about 38 bits, against at most 5 tries a minute.
    private const string Alphabet = "ABCDEFGHJKMNPQRTUVWXYZ234678";
    private const int Length = 8;

    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;
    private readonly ILogger<SetupCode> _logger;
    private readonly Lock _lock = new();
    private string? _code;
    private string _generation = "";
    private int _wrong;
    private DateTimeOffset _waitUntil;

    public SetupCode(IDataProtectionProvider protection, TimeProvider time, ILogger<SetupCode> logger)
    {
        ArgumentNullException.ThrowIfNull(protection);
        _protector = protection.CreateProtector("Contactor.SetupCode");
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Makes a new code, which ends every cookie given for the one before (a live connection opened with one runs on
    /// until it closes), and writes it to the log.
    /// </summary>
    public void Renew()
    {
        string code;
        lock (_lock)
        {
            code = string.Create(Length, 0, static (chars, _) =>
            {
                for (var i = 0; i < chars.Length; i++)
                {
                    chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
                }
            });
            _code = code;
            _generation = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            _wrong = 0;
            _waitUntil = default;
        }

        LogCode(Format(code));
    }

    /// <summary>
    /// Checks a typed code (case, spaces and dashes don't matter) and, when it is right, gives the browser its cookie.
    /// </summary>
    public SetupCodeResult TryAccept(string? typed, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string generation;
        lock (_lock)
        {
            if (_code is null)
            {
                return SetupCodeResult.Wrong;
            }

            var now = _time.GetUtcNow();
            if (now < _waitUntil)
            {
                return SetupCodeResult.Waiting;
            }

            if (!Matches(Normalize(typed), _code))
            {
                if (++_wrong >= WrongBeforeWait)
                {
                    _wrong = 0;
                    _waitUntil = now + Wait;
                    LogWaiting(WrongBeforeWait, Wait.TotalSeconds);
                }

                return SetupCodeResult.Wrong;
            }

            _wrong = 0;
            generation = _generation;
        }

        // Lax, not Strict: coming back from Cloudflare Access's sign-in page is a visit from another site, and must
        // keep the cookie. Secure only over HTTPS: behind cloudflared, and on the local network, the app itself
        // answers plain HTTP.
        context.Response.Cookies.Append(CookieName, _protector.Protect(generation), new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
        });
        return SetupCodeResult.Accepted;
    }

    /// <summary>Whether the request comes from a browser that typed the current code.</summary>
    public bool HasAccepted(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Request.Cookies[CookieName] is not { Length: > 0 } cookie)
        {
            return false;
        }

        string generation;
        try
        {
            generation = _protector.Unprotect(cookie);
        }
        catch (CryptographicException)
        {
            return false;
        }

        lock (_lock)
        {
            return _code is not null && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(generation), Encoding.ASCII.GetBytes(_generation));
        }
    }

    /// <summary>The code as the log shows it, e.g. <c>K7P4-92XM</c>.</summary>
    public static string Format(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return code.Length == Length ? $"{code[..4]}-{code[4..]}" : code;
    }

    private static string Normalize(string? typed)
    {
        if (typed is null)
        {
            return "";
        }

        var builder = new StringBuilder(Length);
        foreach (var c in typed)
        {
            if (char.IsLetterOrDigit(c))
            {
                // At most a little more than a code: anything longer is wrong anyway.
                if (builder.Length > Length)
                {
                    break;
                }

                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }

    private static bool Matches(string typed, string code)
    {
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(typed.PadRight(Length + 1)), Encoding.ASCII.GetBytes(code.PadRight(Length + 1)));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Contactor is being set up. The setup code is {Code}: type it in the setup guide")]
    private partial void LogCode(string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} wrong setup codes: no code is tried for {Seconds} s")]
    private partial void LogWaiting(int count, double seconds);
}
