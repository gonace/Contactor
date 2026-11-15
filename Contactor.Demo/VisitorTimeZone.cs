namespace Contactor.Demo;

/// <summary>
/// The visitor's own time zone, which their copy runs in (the app's <c>TimeZone</c> setting): its schedules, its event
/// log and the Workshop template's days. The page's script reads it from the browser and puts it in a short-lived
/// cookie before the frames load, whose first request starts the copy. Only a time zone name as the IANA database
/// writes them, that this machine knows, is taken; anything else, or none (no script), is UTC.
/// </summary>
public static class VisitorTimeZone
{
    public const string CookieName = "contactor-demo-zone";

    /// <summary>The longest name taken; the IANA database's longest is about 30 characters.</summary>
    public const int MaxLength = 64;

    /// <summary>The cookie's time zone, or UTC.</summary>
    public static TimeZoneInfo Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Find(request.Cookies.TryGetValue(CookieName, out var value) ? value : null);
    }

    /// <summary>The time zone named <paramref name="name"/> (e.g. America/New_York), or UTC.</summary>
    public static TimeZoneInfo Find(string? name)
    {
        // Letters, digits and "/_-+" only, in parts that are never empty: nothing that could leave the time zone
        // folder the machine looks the name up in, nor a Windows name.
        if (name is not { Length: > 0 and <= MaxLength }
            || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '+')
            || name.Split('/').Any(p => p.Length == 0)
            || !char.IsAsciiLetter(name[0]))
        {
            return TimeZoneInfo.Utc;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(name, out var zone) ? zone : TimeZoneInfo.Utc;
    }
}
