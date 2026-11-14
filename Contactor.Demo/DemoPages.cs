using System.Globalization;
using System.Security.Cryptography;

namespace Contactor.Demo;

/// <summary>
/// The demo's own pages, plain HTML without JavaScript: the simulator and the app side by side (two tabs on a phone),
/// "busy", and "stopped". Every colour comes from the app's design tokens, in <c>demo.css</c>.
/// </summary>
public static class DemoPages
{
    /// <summary>How often the busy page tries again by itself.</summary>
    public const int BusyRetrySeconds = 30;

    private static readonly Lazy<byte[]> StylesheetBytes = new(() =>
    {
        using var stream = typeof(DemoPages).Assembly.GetManifestResourceStream("demo.css")
            ?? throw new InvalidOperationException("demo.css isn't in the assembly.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    });

    // A new build gives the stylesheet a new address, so a browser never keeps an old one.
    private static readonly Lazy<string> StylesheetVersion = new(() =>
        Convert.ToHexStringLower(SHA256.HashData(StylesheetBytes.Value))[..12]);

    public static string Page(TimeSpan idleTimeout)
    {
        return Document("Contactor demo", "demo", $"""
            <input type="radio" name="tab" id="tab-app" class="tab-input" checked>
            <input type="radio" name="tab" id="tab-simulator" class="tab-input">
            <header class="demo-header">
                <div class="demo-intro">
                    <h1>Contactor demo</h1>
                    <p class="muted">Your own copy of the app, with a simulated relay board. Nobody else sees it. It's deleted after {Minutes(idleTimeout)} without use.</p>
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
            """);
    }

    public static string Busy()
    {
        var seconds = BusyRetrySeconds.ToString(CultureInfo.InvariantCulture);
        return Document("The demo is busy", "message", $"""
            <main class="card">
                <h1>The demo is busy right now</h1>
                <p class="muted">Everyone's copy is in use. This page tries again by itself every {seconds} seconds.</p>
                <form method="get" action="/">
                    <button type="submit">Try again now</button>
                </form>
            </main>
            """, $"""<meta http-equiv="refresh" content="{seconds}">""");
    }

    /// <summary>In a frame whose copy was stopped: starts again in the whole window, not in the frame.</summary>
    public static string Stopped(TimeSpan idleTimeout)
    {
        return Document("The demo stopped", "message", $"""
            <main class="card">
                <h1>This copy of the demo was stopped</h1>
                <p class="muted">A copy is deleted after {Minutes(idleTimeout)} without use.</p>
                <form method="get" action="/" target="_top">
                    <button type="submit">Start again</button>
                </form>
            </main>
            """);
    }

    /// <summary>In a frame whose copy didn't start.</summary>
    public static string NotStarted()
    {
        return Document("The demo didn't start", "message", """
            <main class="card">
                <h1>Your copy of the demo didn't start</h1>
                <p class="muted">Something went wrong on our side.</p>
                <form method="get" action="/" target="_top">
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

    private static string Minutes(TimeSpan time)
    {
        var minutes = (int)Math.Round(time.TotalMinutes);
        return minutes == 1 ? "1 minute" : string.Create(CultureInfo.InvariantCulture, $"{minutes} minutes");
    }

    private static string Document(string title, string bodyClass, string body, string head = "")
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
            <body class="{bodyClass}">
            {body}
            </body>
            </html>
            """;
    }
}
