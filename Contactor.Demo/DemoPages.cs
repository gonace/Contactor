using System.Globalization;
using System.Security.Cryptography;

namespace Contactor.Demo;

/// <summary>
/// The demo's own pages: the simulator and the app side by side (two tabs on a phone), "busy", and "ended". Only the
/// side-by-side page has a script, <c>demo.js</c>: it tells the demo about clicks and keys and asks whether the visitor
/// is still there. Every colour comes from the app's design tokens, in <c>demo.css</c>.
/// </summary>
public static class DemoPages
{
    /// <summary>How often the busy page tries again by itself.</summary>
    public const int BusyRetrySeconds = 30;

    /// <summary>The longest the page waits to report a click or a key.</summary>
    public static readonly TimeSpan ReportAtMost = TimeSpan.FromMinutes(1);

    private static readonly string[] Fonts = ["ibm-plex-sans-latin-400-normal.woff2", "ibm-plex-sans-latin-600-normal.woff2"];

    private static readonly Lazy<byte[]> StylesheetBytes = new(() => Resource("demo.css"));

    private static readonly Lazy<byte[]> ScriptBytes = new(() => Resource("demo.js"));

    // A new build gives the stylesheet and the script a new address, so a browser never keeps an old one.
    private static readonly Lazy<string> StylesheetVersion = new(() => Version(StylesheetBytes.Value));

    private static readonly Lazy<string> ScriptVersion = new(() => Version(ScriptBytes.Value));

    /// <param name="options">The idle timeout and the count down, which the page's script follows.</param>
    /// <param name="offersTemplate">
    /// Whether the visitor's app is still new, on its setup guide: then "Skip setup" covers the app's frame (not the
    /// simulator's), which can't be used until the visitor chose.
    /// </param>
    public static string Page(DemoOptions options, bool offersTemplate = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        var idle = Seconds(options.IdleTimeout);
        var warning = Seconds(options.IdleWarning);

        // Rounded down: the page may report more often than the demo allows for, never less.
        var report = Seconds(ReportEvery(options));
        return Document("Contactor demo", "demo", $"""
            <input type="radio" name="tab" id="tab-app" class="tab-input" checked>
            <input type="radio" name="tab" id="tab-simulator" class="tab-input">
            <header class="demo-header">
                <div class="demo-intro">
                    <h1>Contactor demo</h1>
                    <p class="muted">Your own copy of the app, with a simulated relay board, reset when you're done.</p>
                </div>
                <div class="demo-tabs">
                    <label for="tab-simulator">Simulator</label>
                    <label for="tab-app">App</label>
                </div>
            </header>
            <main class="demo-frames">
                <iframe class="frame-simulator" title="Simulator" src="{DemoRoutes.SimulatorPrefix}/"></iframe>
                <iframe class="frame-app" title="Contactor" src="{DemoRoutes.AppFrame}"{(offersTemplate ? " inert" : "")}></iframe>
                {(offersTemplate ? TemplateOffer() : "")}
            </main>
            <div class="overlay" id="demo-idle" hidden>
                <div class="card" role="alertdialog" aria-modal="true" aria-labelledby="demo-idle-title" aria-describedby="demo-idle-text">
                    <h2 id="demo-idle-title">Are you still there?</h2>
                    <p class="muted" id="demo-idle-text">Your demo ends in <span id="demo-idle-left">{Clock(options.IdleWarning)}</span>. Click anywhere or press a key to keep it.</p>
                    <button type="button" id="demo-idle-keep">Keep my demo</button>
                </div>
            </div>
            <div class="overlay" id="demo-ended" hidden>
                {EndedCard()}
            </div>
            <script src="{DemoRoutes.ScriptPath}?v={ScriptVersion.Value}" defer></script>
            """, bodyData: $"""data-idle="{idle}" data-warning="{warning}" data-report="{report}" data-activity="{DemoRoutes.ActivityPath}" data-timeout="{DemoRoutes.TimeoutPath}" """);
    }

    public static string Busy()
    {
        var seconds = BusyRetrySeconds.ToString(CultureInfo.InvariantCulture);
        return Document("The demo is busy", "message", $"""
            <main class="card">
                <h1>The demo is busy right now</h1>
                <p class="muted">Everyone's copy is in use. This page tries again by itself every {seconds} seconds.</p>
                <form method="get" action="/" target="_top">
                    <button type="submit">Try again now</button>
                </form>
            </main>
            """, $"""<meta http-equiv="refresh" content="{seconds}">""");
    }

    /// <summary>In a frame whose copy ended: "Start again" starts over in the whole window, not in the frame.</summary>
    public static string Ended()
    {
        return Document("Your demo has ended", "message", $"""
            <main class="card">
                {EndedContent("h1")}
            </main>
            """);
    }

    /// <summary>In a frame whose copy didn't start.</summary>
    public static string NotStarted()
    {
        return Document("The demo didn't start", "message", $"""
            <main class="card">
                <h1>Your copy of the demo didn't start</h1>
                <p class="muted">Something went wrong on our side.</p>
                <form method="post" action="{DemoRoutes.EndPath}" target="_top">
                    <button type="submit">Try again</button>
                </form>
            </main>
            """);
    }

    /// <summary>
    /// How often the page reports clicks and keys at most, so how late the demo may hear of one: at least twice within
    /// the idle timeout, so the demo never thinks a visitor left whose page doesn't, and at least twice within the count
    /// down, so a page whose count down ran out is never taken for one still used in another tab (see
    /// <see cref="VisitorCopies.EndIfIdleAsync"/>).
    /// </summary>
    public static TimeSpan ReportEvery(DemoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return TimeSpan.FromTicks(Math.Min(ReportAtMost.Ticks, Math.Min(options.IdleTimeout.Ticks / 2, options.IdleWarning.Ticks / 2)));
    }

    /// <summary>demo.css, as UTF-8.</summary>
    public static byte[] Stylesheet()
    {
        return StylesheetBytes.Value;
    }

    /// <summary>demo.js, as UTF-8.</summary>
    public static byte[] Script()
    {
        return ScriptBytes.Value;
    }

    /// <summary>Whether <paramref name="name"/> (<c>/</c> and a file name) is one of the stylesheet's fonts.</summary>
    public static bool HasFont(PathString name)
    {
        return name.HasValue && Fonts.Any(f => name.Value.Equals("/" + f, StringComparison.Ordinal));
    }

    /// <summary>One of the stylesheet's fonts, by <see cref="HasFont"/>'s name.</summary>
    public static byte[] Font(PathString name)
    {
        return HasFont(name) ? Resource("fonts" + name.Value) : throw new ArgumentException("Not one of the fonts.", nameof(name));
    }

    // Over the app's frame only: the simulator stays usable. Two forms, so it works without the script; the script only
    // hides it at once for "Go through the setup myself", and says that the template is on its way.
    private static string TemplateOffer()
    {
        return $$"""
            <section class="template-offer" id="demo-template" aria-labelledby="demo-template-title">
                <div class="card">
                    <h2 id="demo-template-title">Skip setup: start from a template</h2>
                    <p class="muted" id="demo-template-text" aria-live="polite">The app is new, so it opens on its setup guide. Skip it with a ready-made workshop, or set it up yourself.</p>
                    <form method="post" action="{{DemoRoutes.TemplatePath}}" target="_top" id="demo-template-form">
                        <input type="hidden" name="template" value="{{WorkshopTemplate.Id}}">
                        <button type="submit" class="template-choice">
                            <span class="template-name">Workshop</span>
                            <span class="template-text">A fan heater, an air compressor, lights, ventilation and an outdoor light, used for three days by Alex and Sam.</span>
                        </button>
                    </form>
                    <form method="post" action="{{DemoRoutes.SetupPath}}" target="_top" id="demo-setup-form">
                        <button type="submit" class="secondary">Go through the setup myself</button>
                    </form>
                </div>
            </section>
            """;
    }

    private static string EndedCard()
    {
        return $"""
            <div class="card" role="alertdialog" aria-modal="true" aria-labelledby="demo-ended-title" aria-describedby="demo-ended-text">
                {EndedContent("h2")}
            </div>
            """;
    }

    // "Start again" ends what is left of the copy and starts over at the page, in the whole window.
    private static string EndedContent(string heading)
    {
        return $"""
            <{heading} id="demo-ended-title">Your demo has ended</{heading}>
            <p class="muted" id="demo-ended-text">Your copy was deleted. Start again for a fresh one.</p>
            <form method="post" action="{DemoRoutes.EndPath}" target="_top">
                <button type="submit" id="demo-ended-start">Start again</button>
            </form>
            """;
    }

    private static byte[] Resource(string name)
    {
        using var stream = typeof(DemoPages).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} isn't in the assembly.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string Version(byte[] bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes))[..12];
    }

    private static string Seconds(TimeSpan time)
    {
        return ((long)time.TotalSeconds).ToString(CultureInfo.InvariantCulture);
    }

    private static string Clock(TimeSpan time)
    {
        var seconds = (long)time.TotalSeconds;
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }

    private static string Document(string title, string bodyClass, string body, string head = "", string bodyData = "")
    {
        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            {head}
            <title>{title}</title>
            <link rel="stylesheet" href="{DemoRoutes.StylesheetPath}?v={StylesheetVersion.Value}">
            </head>
            <body class="{bodyClass}" {bodyData}>
            {body}
            </body>
            </html>
            """;
    }
}
