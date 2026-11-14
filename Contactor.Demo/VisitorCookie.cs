using System.Security.Cryptography;

namespace Contactor.Demo;

/// <summary>
/// The cookie that tells which copy is a visitor's: a random id, 128 bits as 32 lower-case hex digits. It only finds
/// the copy; nothing is made of it (the copy's data folder has an id of its own). Never logged in full.
/// </summary>
public static class VisitorCookie
{
    public const string Name = "contactor-demo";

    private const int IdLength = 32;

    /// <summary>A new visitor's id.</summary>
    public static string NewId()
    {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(IdLength / 2));
    }

    /// <summary>The visitor's id from the request, or null when there is none or it isn't one the demo could have given.</summary>
    public static string? Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Cookies.TryGetValue(Name, out var value) && IsWellFormed(value) ? value : null;
    }

    public static bool IsWellFormed(string? value)
    {
        return value is { Length: IdLength } && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
    }

    /// <summary>Gives the browser its id: HttpOnly, Lax, Secure over HTTPS, for the whole site, until the browser closes.</summary>
    public static void Give(HttpContext context, string id)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Cookies.Append(Name, id, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
        });
    }

    /// <summary>The first characters of an id, enough to tell copies apart in the log and useless to anyone reading it.</summary>
    public static string ForLog(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return id.Length <= 6 ? id : id[..6];
    }
}
