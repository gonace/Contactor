using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Yarp.ReverseProxy.Forwarder;

namespace Contactor.Demo;

/// <summary>
/// The demo's only public endpoint: gives a new visitor a copy and a cookie, shows the page, and passes every other
/// request (Blazor's live connection included) to the visitor's own copy, found by the cookie and nothing else, so a
/// visitor never reaches another's copy.
/// </summary>
public sealed partial class DemoProxy : IDisposable
{
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
                await StylesheetAsync(context);
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
        var visitorId = VisitorCookie.Read(context.Request);
        if ((visitorId is null || !_copies.Touch(visitorId)) && HttpMethods.IsHead(context.Request.Method))
        {
            // A check that the site answers (a monitor, a link preview) takes no visitor's place.
            context.Response.ContentType = "text/html; charset=utf-8";
            return;
        }

        if (visitorId is null || !_copies.Touch(visitorId))
        {
            // A new visitor, or one whose copy was stopped: a new copy with a new id, never one the browser chose.
            var copy = _copies.TryStart();
            if (copy is null)
            {
                LogBusy(_logger, _options.MaxVisitors);
                context.Response.Headers.RetryAfter = DemoPages.BusyRetrySeconds.ToString(CultureInfo.InvariantCulture);
                await HtmlAsync(context, StatusCodes.Status503ServiceUnavailable, DemoPages.Busy());
                return;
            }

            VisitorCookie.Give(context, copy.VisitorId);
        }

        await HtmlAsync(context, StatusCodes.Status200OK, DemoPages.Page(_options.IdleTimeout));
    }

    private static async Task StylesheetAsync(HttpContext context)
    {
        var css = DemoPages.Stylesheet();
        context.Response.ContentType = "text/css; charset=utf-8";
        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        context.Response.ContentLength = css.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(css, context.RequestAborted);
        }
    }

    private async Task ForwardAsync(HttpContext context, DemoRoute route)
    {
        var visitorId = VisitorCookie.Read(context.Request);
        var copy = visitorId is null ? null : _copies.Begin(visitorId);
        if (copy is null)
        {
            await NoCopyAsync(context, DemoPages.Stopped(_options.IdleTimeout));
            return;
        }

        try
        {
            var part = route.Target == DemoTarget.App ? CopyPart.App : CopyPart.Simulator;
            var program = copy.Part(part);
            try
            {
                await copy.Started.WaitAsync(_options.StartTimeout, _time, context.RequestAborted);
                await program.Listening.WaitAsync(_options.StartTimeout, _time, context.RequestAborted);
            }
            catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
            {
                await NoCopyAsync(context, DemoPages.NotStarted());
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
        finally
        {
            _copies.End(copy);
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

    /// <summary>Requests to the app: as they came, without the demo's cookie, which is none of the copy's business.</summary>
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
                .Where(c => !c.StartsWith(VisitorCookie.Name + "=", StringComparison.Ordinal))
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
