using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Yarp.ReverseProxy.Forwarder;

namespace Contactor.Demo;

/// <summary>
/// The demo's only public endpoint: gives a new visitor a cookie and shows the page, starts their copy when the page's
/// frames first ask for it, and passes every other request (Blazor's live connection included) to the visitor's own
/// copy, found by the cookie and nothing else, so a visitor never reaches another's copy.
/// </summary>
public sealed partial class DemoProxy : IDisposable
{
    // The template's form is one short field.
    private const int MaxFormBytes = 1024;

    private static readonly ForwarderRequestConfig RequestConfig = new() { ActivityTimeout = TimeSpan.FromMinutes(2) };

    private readonly VisitorCopies _copies;
    private readonly IHttpForwarder _forwarder;
    private readonly DemoOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<DemoProxy> _logger;
    private readonly HttpMessageInvoker _client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    });

    public DemoProxy(VisitorCopies copies, IHttpForwarder forwarder, IOptions<DemoOptions> options, TimeProvider time, ILogger<DemoProxy> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _copies = copies;
        _forwarder = forwarder;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var route = DemoRoutes.Find(context.Request);
        switch (route.Target)
        {
            case DemoTarget.Page:
                await PageAsync(context);
                break;
            case DemoTarget.Stylesheet:
                await FileAsync(context, "text/css; charset=utf-8", DemoPages.Stylesheet());
                break;
            case DemoTarget.Script:
                await FileAsync(context, "text/javascript; charset=utf-8", DemoPages.Script());
                break;
            case DemoTarget.Font:
                await FileAsync(context, "font/woff2", DemoPages.Font(route.Path));
                break;
            case DemoTarget.Activity:
                Activity(context);
                break;
            case DemoTarget.End:
                await EndAsync(context);
                break;
            case DemoTarget.Timeout:
                await TimeoutAsync(context);
                break;
            case DemoTarget.Template:
                await TemplateAsync(context);
                break;
            case DemoTarget.Setup:
                Setup(context);
                break;
            case DemoTarget.App:
            case DemoTarget.Simulator:
                await ForwardAsync(context, route);
                break;
            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                break;
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private async Task PageAsync(HttpContext context)
    {
        NoStore(context.Response);
        if (HttpMethods.IsHead(context.Request.Method))
        {
            // A check that the site answers (a monitor, a link preview) takes no visitor's place.
            context.Response.ContentType = "text/html; charset=utf-8";
            return;
        }

        // A bot or a link preview that reads the page gets an id at most; only the page's frames start the copy.
        var visit = _copies.Visit(VisitorCookie.Read(context.Request));
        if (visit.Kind == VisitKind.Busy)
        {
            await BusyAsync(context, page: true);
            return;
        }

        if (visit.NewId is { } id)
        {
            VisitorCookie.Give(context, id);
        }

        await HtmlAsync(context, StatusCodes.Status200OK, DemoPages.Page(_options, visit.OffersTemplate));
    }

    // The page, or a frame of it: "busy" and its "Try again now". Anything else just gets the status.
    private async Task BusyAsync(HttpContext context, bool page)
    {
        LogBusy(_logger, _options.MaxVisitors);
        NoStore(context.Response);
        context.Response.Headers.RetryAfter = DemoPages.BusyRetrySeconds.ToString(CultureInfo.InvariantCulture);
        if (page || DemoRoutes.IsDocument(context.Request))
        {
            await HtmlAsync(context, StatusCodes.Status503ServiceUnavailable, DemoPages.Busy());
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
    }

    private static async Task FileAsync(HttpContext context, string contentType, byte[] bytes)
    {
        context.Response.ContentType = contentType;
        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        context.Response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        }
    }

    // The page reports a click or a key: 204 while the copy is there, 404 once it is gone, so the page says it ended.
    private void Activity(HttpContext context)
    {
        NoStore(context.Response);
        if (!DemoRoutes.IsFromThisSite(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var visitorId = VisitorCookie.Read(context.Request);
        context.Response.StatusCode = visitorId is not null && _copies.Active(visitorId)
            ? StatusCodes.Status204NoContent
            : StatusCodes.Status404NotFound;
    }

    // Ends the copy of the cookie's visitor, never another's: "Start again" (or "Try again"), which then starts over at
    // the page.
    private async Task EndAsync(HttpContext context)
    {
        NoStore(context.Response);
        if (!DemoRoutes.IsFromThisSite(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (VisitorCookie.Read(context.Request) is { } visitorId)
        {
            await _copies.EndAsync(visitorId);
        }

        if (DemoRoutes.IsDocument(context.Request, post: true))
        {
            context.Response.StatusCode = StatusCodes.Status303SeeOther;
            context.Response.Headers.Location = "/";
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }
    }

    // The page's count down ran out: 204 when the copy ended (or was gone already), so the page says so; 409 when the
    // visitor used it since in another tab, so the page keeps it and counts again.
    private async Task TimeoutAsync(HttpContext context)
    {
        NoStore(context.Response);
        if (!DemoRoutes.IsFromThisSite(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var ended = VisitorCookie.Read(context.Request) is not { } visitorId || await _copies.EndIfIdleAsync(visitorId);
        context.Response.StatusCode = ended ? StatusCodes.Status204NoContent : StatusCodes.Status409Conflict;
    }

    // "Skip setup: start from a template", the page's form: fills the cookie's own copy, never another's, then shows the
    // page again (303), whose app frame opens on the relays. To a fetch: 204 when applied, 409 when the copy was left as
    // it was, 404 without a copy, 503 when it didn't start.
    private async Task TemplateAsync(HttpContext context)
    {
        NoStore(context.Response);
        if (!DemoRoutes.IsFromThisSite(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!await IsWorkshopAsync(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Not cancelled with the request: once the app is stopped, it is filled and started again whatever the browser does.
        var result = VisitorCookie.Read(context.Request) is { } visitorId ? await _copies.ApplyTemplateAsync(visitorId) : TemplateResult.None;
        AfterChoice(context, result switch
        {
            TemplateResult.Applied => StatusCodes.Status204NoContent,
            TemplateResult.Refused => StatusCodes.Status409Conflict,
            TemplateResult.DidntStart => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status404NotFound,
        });
    }

    // "Go through the setup myself": the page doesn't offer the template again for the cookie's copy.
    private void Setup(HttpContext context)
    {
        NoStore(context.Response);
        if (!DemoRoutes.IsFromThisSite(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var declined = VisitorCookie.Read(context.Request) is { } visitorId && _copies.DeclineTemplate(visitorId);
        AfterChoice(context, declined ? StatusCodes.Status204NoContent : StatusCodes.Status404NotFound);
    }

    // A form gets the page again, which shows what came of it; a fetch gets the status.
    private static void AfterChoice(HttpContext context, int status)
    {
        if (DemoRoutes.IsDocument(context.Request, post: true))
        {
            context.Response.StatusCode = StatusCodes.Status303SeeOther;
            context.Response.Headers.Location = "/";
        }
        else
        {
            context.Response.StatusCode = status;
        }
    }

    // The only template so far. A form sends it by name, so a later one is another value. Only a small plain form is
    // read: nothing a visitor sends here is kept, so nothing big is taken in.
    private static async Task<bool> IsWorkshopAsync(HttpRequest request)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type)
            || !type.MediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        {
            size.MaxRequestBodySize = MaxFormBytes;
        }

        try
        {
            var form = await request.ReadFormAsync(
                new FormOptions { ValueCountLimit = 4, KeyLengthLimit = 32, ValueLengthLimit = 64, BufferBodyLengthLimit = MaxFormBytes },
                request.HttpContext.RequestAborted);
            return form["template"] == WorkshopTemplate.Id;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or BadHttpRequestException)
        {
            return false;
        }
    }

    private async Task ForwardAsync(HttpContext context, DemoRoute route)
    {
        var visitorId = VisitorCookie.Read(context.Request);
        var use = visitorId is null ? new Use(UseKind.None) : _copies.Use(visitorId, VisitorTimeZone.Read(context.Request));
        if (use.Kind == UseKind.Busy)
        {
            await BusyAsync(context, page: false);
            return;
        }

        if (use.Copy is not { } copy)
        {
            await NoCopyAsync(context, DemoPages.Ended());
            return;
        }

        var part = route.Target == DemoTarget.App ? CopyPart.App : CopyPart.Simulator;
        var program = copy.Part(part);
        try
        {
            await copy.Started.WaitAsync(_options.StartTimeout, _time, context.RequestAborted);
            await program.Listening.WaitAsync(_options.StartTimeout, _time, context.RequestAborted);
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            // Ended while the request waited for it: the visitor sees that, not an error. A copy that failed to start,
            // or whose program was given up, is stopped too, but says it didn't start.
            await NoCopyAsync(context, copy.Stopped.IsCompleted && !copy.DidntStart ? DemoPages.Ended() : DemoPages.NotStarted());
            return;
        }

        var destination = string.Create(CultureInfo.InvariantCulture, $"http://{VisitorCopies.Host}:{program.Program.Port}");
        var transformer = part == CopyPart.App ? AppTransformer.Instance : new SimulatorTransformer(route.Path);
        var error = await _forwarder.SendAsync(context, destination, _client, RequestConfig, transformer);
        if (error != ForwarderError.None && !context.RequestAborted.IsCancellationRequested)
        {
            LogForwardFailed(_logger, copy.Visitor, part, error);
        }
    }

    private static async Task NoCopyAsync(HttpContext context, string page)
    {
        NoStore(context.Response);
        if (DemoRoutes.IsDocument(context.Request))
        {
            await HtmlAsync(context, StatusCodes.Status404NotFound, page);
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
    }

    private static void NoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
    }

    private static async Task HtmlAsync(HttpContext context, int status, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The demo is full ({Max} visitors): a new visitor got the busy page")]
    private static partial void LogBusy(ILogger logger, int max);

    [LoggerMessage(Level = LogLevel.Information, Message = "A request to the {Part} of visitor {Visitor} failed: {Error}")]
    private static partial void LogForwardFailed(ILogger logger, string visitor, CopyPart part, ForwarderError error);

    /// <summary>Requests to the app: as they came, without the demo's cookies, which are none of the copy's business.</summary>
    private class AppTransformer : HttpTransformer
    {
        public static AppTransformer Instance { get; } = new();

        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext,
            HttpRequestMessage proxyRequest,
            string destinationPrefix,
            CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);
            WithoutVisitorCookie(proxyRequest);
        }

        private static void WithoutVisitorCookie(HttpRequestMessage request)
        {
            if (!request.Headers.TryGetValues(HeaderNames.Cookie, out var values))
            {
                return;
            }

            var kept = values
                .SelectMany(v => v.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                .Where(c => !c.StartsWith(VisitorCookie.Name + "=", StringComparison.Ordinal)
                    && !c.StartsWith(VisitorTimeZone.CookieName + "=", StringComparison.Ordinal))
                .ToList();
            request.Headers.Remove(HeaderNames.Cookie);
            if (kept.Count > 0)
            {
                request.Headers.TryAddWithoutValidation(HeaderNames.Cookie, string.Join("; ", kept));
            }
        }
    }

    /// <summary>
    /// Requests to the simulator: without the prefix, and its page's <c>&lt;base href&gt;</c> pointed back under the
    /// prefix, so its files, its live connection and its links all come back through it.
    /// </summary>
    private sealed class SimulatorTransformer : AppTransformer
    {
        private readonly PathString _path;

        public SimulatorTransformer(PathString path)
        {
            _path = path;
        }

        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext,
            HttpRequestMessage proxyRequest,
            string destinationPrefix,
            CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);
            proxyRequest.RequestUri = RequestUtilities.MakeDestinationAddress(destinationPrefix, _path, httpContext.Request.QueryString);

            // A page comes back uncompressed, so its base can be changed.
            if (httpContext.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                proxyRequest.Headers.AcceptEncoding.Clear();
            }
        }

        public override async ValueTask<bool> TransformResponseAsync(
            HttpContext httpContext,
            HttpResponseMessage? proxyResponse,
            CancellationToken cancellationToken)
        {
            var copyBody = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);
            if (proxyResponse is null
                || proxyResponse.Content.Headers.ContentType?.MediaType != "text/html"
                || proxyResponse.Content.Headers.ContentEncoding.Count > 0)
            {
                return copyBody;
            }

            var html = await proxyResponse.Content.ReadAsStringAsync(cancellationToken);
            var bytes = Encoding.UTF8.GetBytes(DemoRoutes.RebaseSimulatorPage(html));
            httpContext.Response.Headers.Remove(HeaderNames.ContentLength);
            httpContext.Response.ContentLength = bytes.Length;
            await httpContext.Response.Body.WriteAsync(bytes, cancellationToken);
            return false;
        }
    }
}
