namespace Contactor.Web.Features.Setup;

/// <summary>
/// What answers while the installation is being set up (no sign-in chosen): the static files, the page that asks for
/// the setup code, and, for a browser that typed it (<see cref="SetupCode"/>), the setup guide and its live connection.
/// Every other page opens the code page; anything else is denied.
/// </summary>
public sealed class SetupMode
{
    /// <summary>The page that asks for the code (<c>SetupCodePage</c>; a test checks its route is this).</summary>
    public const string CodePath = "/setup/code";
    public const string GuidePath = "/setup";

    // What the pages are made of; none of it tells anything about the installation.
    private static readonly HashSet<string> StaticExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".css", ".js", ".woff2", ".png", ".svg", ".ico", ".webmanifest" };

    private readonly SetupCode _code;

    public SetupMode(SetupCode code)
    {
        _code = code;
    }

    /// <summary>
    /// Whether the request may go on. When it may not, the response is written: a page goes to the code page (or the
    /// guide, once the code was typed), anything else is denied.
    /// </summary>
    public async Task<bool> LetThroughAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = context.Request.Path;
        if (IsStatic(path) || path.Equals(CodePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var accepted = _code.HasAccepted(context);
        if (accepted && (path.Equals(GuidePath, StringComparison.OrdinalIgnoreCase) || path.StartsWithSegments("/_blazor")))
        {
            return true;
        }

        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.Redirect(accepted ? GuidePath : CodePath);
            return false;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("Access denied.", context.RequestAborted);
        return false;
    }

    private static bool IsStatic(PathString path)
    {
        return path.StartsWithSegments("/_framework")
            || (path.HasValue && StaticExtensions.Contains(Path.GetExtension(path.Value)));
    }
}
