namespace Contactor.Web.Features.Identity;

/// <summary>
/// At start: refuses to start with ASP.NET Core's forwarded headers on. With them on, the app takes the visitor's
/// address from <c>X-Forwarded-For</c>, which anyone can write, and a made-up address could pass "Only on this network".
/// </summary>
public static partial class ForwardedHeadersCheck
{
    /// <summary>The configuration key ASP.NET Core reads; <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c> sets it.</summary>
    public const string Key = "ForwardedHeaders_Enabled";

    public const string Message =
        "Forwarded headers are on (the setting ForwardedHeaders_Enabled, e.g. ASPNETCORE_FORWARDEDHEADERS_ENABLED). Contactor reads the visitor's address itself; turn the setting off.";

    /// <summary>Logs an error and throws when the setting is on, as ASP.NET Core reads it: "true", in any case.</summary>
    public static void CheckForwardedHeadersOff(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (string.Equals("true", app.Configuration[Key], StringComparison.OrdinalIgnoreCase))
        {
            LogForwardedHeadersOn(app.Logger);
            throw new InvalidOperationException(Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = Message)]
    private static partial void LogForwardedHeadersOn(ILogger logger);
}
