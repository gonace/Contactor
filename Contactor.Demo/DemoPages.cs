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

    /// <summary>How often the page reports clicks and keys at most.</summary>
    public static readonly TimeSpan ReportEvery = TimeSpan.FromMinutes(1);

    private static readonly string[] Fonts = ["ibm-plex-sans-latin-400-normal.woff2", "ibm-plex-sans-latin-600-normal.woff2"];

    private static readonly Lazy<byte[]> StylesheetBytes = new(() => Resource("demo.css"));

    private static readonly Lazy<byte[]> ScriptBytes = new(() => Resource("demo.js"));

    // A new build gives the stylesheet and the script a new address, so a browser never keeps an old one.
    private static readonly Lazy<string> StylesheetVersion = new(() => Version(StylesheetBytes.Value));

    private static readonly Lazy<string> ScriptVersion = new(() => Version(ScriptBytes.Value));

    /// <param name="options">The idle timeout and the count down, which the page's script follows.</param>
    public static string Page(DemoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var idle = Seconds(options.IdleTimeout);
        var warning = Seconds(options.IdleWarning);

        // At least twice within the idle timeout, so the demo never thinks a visitor left whose page doesn't.
        var report = Seconds(TimeSpan.FromTicks(Math.Min(ReportEvery.Ticks, options.IdleTimeout.Ticks / 2)));
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
                <iframe class="frame-app" title="Contactor" src="{DemoRoutes.AppFrame}"></iframe>
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
            """, bodyData: $"""data-idle="{idle}" data-warning="{warning}" data-report="{report}" data-activity="{DemoRoutes.ActivityPath}" data-end="{DemoRoutes.EndPath}" """);
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
