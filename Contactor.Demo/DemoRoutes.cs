namespace Contactor.Demo;

/// <summary>What a request is for.</summary>
public enum DemoTarget
{
    /// <summary>The demo's page: the simulator and the app side by side (or the busy page).</summary>
    Page,

    /// <summary>The demo's stylesheet, or one of its fonts.</summary>
    Stylesheet,

    /// <summary>The page's script: tells the demo about clicks and keys, and asks whether the visitor is still there.</summary>
    Script,

    /// <summary>A font of the stylesheet's, under <see cref="DemoRoutes.FontsPrefix"/>.</summary>
    Font,

    /// <summary>The page reports a click or a key (a POST).</summary>
    Activity,

    /// <summary>The visitor's copy ends: "Start again", or "Try again" when it didn't start (a POST).</summary>
    End,

    /// <summary>
    /// The page's count down ran out (a POST): the copy ends unless the visitor used it since in another tab.
    /// </summary>
    Timeout,

    /// <summary>"Skip setup: start from a template" (a POST).</summary>
    Template,

    /// <summary>"Go through the setup myself" (a POST).</summary>
    Setup,

    /// <summary>The visitor's own app.</summary>
    App,

    /// <summary>The visitor's own simulator, under <see cref="DemoRoutes.SimulatorPrefix"/>.</summary>
    Simulator,

    /// <summary>Nothing a visitor may reach.</summary>
    NotFound,
}

/// <param name="Path">The path the copy's program gets: the simulator's without the prefix.</param>
public readonly record struct DemoRoute(DemoTarget Target, PathString Path);

/// <summary>
/// How one host name reaches both of a visitor's programs. Neither can be given a path base from its settings, and the
/// app's pages and redirects use paths from <c>/</c>, so the app keeps the whole site, and the demo takes only
/// <c>/demo/</c>, which the app has no page under:
/// <list type="bullet">
/// <item><c>/</c> opened as a page of its own (a browser's top-level navigation) is the demo's page. In the page's
/// frame, the app's <c>/</c> is the app's (the browser says it is for a frame; the frame's address also says so).</item>
/// <item><c>/demo/simulator/…</c> is the simulator, without the prefix; the proxy points its page's
/// <c>&lt;base href&gt;</c> there, so its files and its live connection come back the same way.</item>
/// <item>Everything else is the app's, except what the app only answers on its own machine (the proxy connects from
/// there): <c>/healthz</c>, <c>/setup/reload</c> and the development checks. A GET of <c>/healthz</c> is the demo's
/// own (<see cref="DemoServiceCollectionExtensions.HealthPath"/>), for its own machine only.</item>
/// </list>
/// </summary>
public static class DemoRoutes
{
    public const string Prefix = "/demo";
    public const string SimulatorPrefix = "/demo/simulator";
    public const string StylesheetPath = "/demo/demo.css";
    public const string ScriptPath = "/demo/demo.js";
    public const string FontsPrefix = "/demo/fonts";
    public const string ActivityPath = "/demo/activity";
    public const string EndPath = "/demo/end";
    public const string TimeoutPath = "/demo/timeout";
    public const string TemplatePath = "/demo/template";
    public const string SetupPath = "/demo/setup";

    /// <summary>The app frame's address: its query tells the app's <c>/</c> from the page also where a browser doesn't say.</summary>
    public const string AppFrame = "/?demo-frame";

    private const string FrameQuery = "demo-frame";
    private const string SimulatorBase = "<base href=\"/\"";
    private const string PrefixedSimulatorBase = "<base href=\"" + SimulatorPrefix + "/\"";

    private static readonly string[] AppOnlyForItsMachine = ["/healthz", "/setup/reload", "/health", "/alive"];
    private static readonly string[] SimulatorOnlyForItsMachine = ["/health", "/alive"];

    public static DemoRoute Find(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = request.Path;
        if (path.StartsWithSegments(SimulatorPrefix, StringComparison.OrdinalIgnoreCase, out var rest))
        {
            var forSimulator = rest.HasValue ? rest : new PathString("/");
            return ForItsMachine(forSimulator, SimulatorOnlyForItsMachine)
                ? new DemoRoute(DemoTarget.NotFound, path)
                : new DemoRoute(DemoTarget.Simulator, forSimulator);
        }

        if (path.Equals(StylesheetPath, StringComparison.OrdinalIgnoreCase))
        {
            return new DemoRoute(DemoTarget.Stylesheet, path);
        }

        if (path.Equals(ScriptPath, StringComparison.OrdinalIgnoreCase))
        {
            return new DemoRoute(DemoTarget.Script, path);
        }

        if (path.StartsWithSegments(FontsPrefix, StringComparison.OrdinalIgnoreCase, out var font) && DemoPages.HasFont(font))
        {
            return new DemoRoute(DemoTarget.Font, font);
        }

        if (HttpMethods.IsPost(request.Method) && path.Equals(ActivityPath, StringComparison.OrdinalIgnoreCase))
        {
            return new DemoRoute(DemoTarget.Activity, path);
        }

        if (HttpMethods.IsPost(request.Method) && path.Equals(EndPath, StringComparison.OrdinalIgnoreCase))
        {
            return new DemoRoute(DemoTarget.End, path);
        }

        if (HttpMethods.IsPost(request.Method) && path.Equals(TimeoutPath, StringComparison.OrdinalIgnoreCase))
        {
            return new DemoRoute(DemoTarget.Timeout, path);
        }

        if (HttpMethods.IsPost(request.Method) && path.Equals(TemplatePath, StringComparison.OrdinalIgnoreCase))
        {
            return new DemoRoute(DemoTarget.Template, path);
        }

        if (HttpMethods.IsPost(request.Method) && path.Equals(SetupPath, StringComparison.OrdinalIgnoreCase))
        {
            return new DemoRoute(DemoTarget.Setup, path);
        }

        if (path.StartsWithSegments(Prefix, StringComparison.OrdinalIgnoreCase) || ForItsMachine(path, AppOnlyForItsMachine))
        {
            return new DemoRoute(DemoTarget.NotFound, path);
        }

        return IsPage(request) ? new DemoRoute(DemoTarget.Page, path) : new DemoRoute(DemoTarget.App, path);
    }

    /// <summary>
    /// Whether a POST comes from the demo's own pages: a browser says where a request comes from, and another site
    /// may not end a visitor's copy or keep it going. Every browser the demo's pages work in sends <c>Sec-Fetch-Site</c>
    /// or <c>Origin</c> with a POST, so a request with neither is refused too.
    /// </summary>
    public static bool IsFromThisSite(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0)
        {
            return site == "same-origin";
        }

        var origin = request.Headers.Origin.ToString();
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a browser asks for a page to show in a frame or a window, rather than a file, a fetch or a connection:
    /// such a request without a copy gets a page saying so. With <paramref name="post"/>, a form's POST counts too.
    /// </summary>
    public static bool IsDocument(HttpRequest request, bool post = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!(HttpMethods.IsGet(request.Method) || (post && HttpMethods.IsPost(request.Method))))
        {
            return false;
        }

        var destination = request.Headers["Sec-Fetch-Dest"].ToString();
        return destination.Length == 0
            ? request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase)
            : destination is "document" or "iframe" or "frame";
    }

    /// <summary>
    /// The simulator's page with its base under <see cref="SimulatorPrefix"/>: the first <c>&lt;base href="/"</c>, the
    /// one in its head. Its files, its live connection and its links are relative to it.
    /// </summary>
    public static string RebaseSimulatorPage(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var at = html.IndexOf(SimulatorBase, StringComparison.Ordinal);
        return at < 0 ? html : string.Concat(html.AsSpan(0, at), PrefixedSimulatorBase, html.AsSpan(at + SimulatorBase.Length));
    }

    private static bool IsPage(HttpRequest request)
    {
        if (!(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            || request.Path != "/"
            || request.Query.ContainsKey(FrameQuery))
        {
            return false;
        }

        // A browser that doesn't say (before 2023) gets the page, and its frame the app by the frame's query.
        var destination = request.Headers["Sec-Fetch-Dest"].ToString();
        return destination.Length == 0 || destination == "document";
    }

    private static bool ForItsMachine(PathString path, string[] paths)
    {
        return paths.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));
    }
}
