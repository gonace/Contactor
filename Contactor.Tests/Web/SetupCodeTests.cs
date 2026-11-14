using System.Net;
using System.Text.RegularExpressions;
using Contactor.Web.Features.Identity;
using Contactor.Web.Features.Setup;
using Contactor.Web.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Contactor.Tests.Web;

/// <summary>The setup code that keeps a new installation to whoever installed it.</summary>
public sealed partial class SetupCodeTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_AcceptTheCode_WhateverItsCaseSpacesAndDashes()
    {
        var (code, logs) = Create(new ManualTimeProvider());
        code.Renew();
        var shown = LoggedCode(logs);
        var typed = $" {shown.ToLowerInvariant().Replace("-", " - ", StringComparison.Ordinal)} ";

        var context = new DefaultHttpContext();
        Assert.Equal(SetupCodeResult.Accepted, code.TryAccept(typed, context));
        Assert.True(code.HasAccepted(WithCookieFrom(context)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABCD-EFGH")]
    [InlineData("ÅÄÖ-ÅÄÖ-ÅÄÖ")]
    public void Should_RefuseAWrongCode(string? typed)
    {
        var (code, _) = Create(new ManualTimeProvider());
        code.Renew();

        var context = new DefaultHttpContext();
        Assert.Equal(SetupCodeResult.Wrong, code.TryAccept(typed, context));
        Assert.False(code.HasAccepted(WithCookieFrom(context)));
    }

    [Fact]
    public void Should_RefuseAnything_BeforeACodeIsMade()
    {
        var (code, _) = Create(new ManualTimeProvider());

        Assert.Equal(SetupCodeResult.Wrong, code.TryAccept("", new DefaultHttpContext()));
    }

    // Guessing gets 5 tries a minute: even the right code waits until the minute has passed.
    [Fact]
    public void Should_Wait_AfterFiveWrongCodes()
    {
        var time = new ManualTimeProvider();
        var (code, logs) = Create(time);
        code.Renew();
        var right = LoggedCode(logs);

        for (var i = 0; i < SetupCode.WrongBeforeWait; i++)
        {
            Assert.Equal(SetupCodeResult.Wrong, code.TryAccept("WRONG-CODE", new DefaultHttpContext()));
        }

        Assert.Equal(SetupCodeResult.Waiting, code.TryAccept(right, new DefaultHttpContext()));
        time.Advance(SetupCode.Wait - TimeSpan.FromSeconds(1));
        Assert.Equal(SetupCodeResult.Waiting, code.TryAccept(right, new DefaultHttpContext()));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SetupCodeResult.Accepted, code.TryAccept(right, new DefaultHttpContext()));
    }

    // A restart makes a new code: a browser let in by the old one is out again.
    [Fact]
    public void Should_EndTheCookie_WhenANewCodeIsMade()
    {
        var (code, logs) = Create(new ManualTimeProvider());
        code.Renew();
        var context = new DefaultHttpContext();
        code.TryAccept(LoggedCode(logs), context);
        var browser = WithCookieFrom(context);

        code.Renew();

        Assert.False(code.HasAccepted(browser));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-signed")]
    [InlineData("CfDJ8forged")]
    public void Should_RefuseACookieTheAppDidNotSign(string cookie)
    {
        var (code, _) = Create(new ManualTimeProvider());
        code.Renew();
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{SetupCode.CookieName}={cookie}";

        Assert.False(code.HasAccepted(context));
    }

    [Fact]
    public async Task Should_OpenOnlyTheCodePage_OnANewInstallation()
    {
        await using var app = NewInstallation(new LogCapture());
        using var http = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (var page in new[] { "/", "/setup", "/settings", "/events" })
        {
            using var response = await http.GetAsync(page, Cancel);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(SetupMode.CodePath, response.Headers.Location?.OriginalString);
        }

        using var backup = await http.GetAsync("/settings/backup", Cancel);
        Assert.Equal(HttpStatusCode.Redirect, backup.StatusCode);
        using var post = await http.PostAsync("/setup", content: null, Cancel);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        using var blazor = await http.PostAsync("/_blazor/negotiate?negotiateVersion=1", content: null, Cancel);
        Assert.Equal(HttpStatusCode.Forbidden, blazor.StatusCode);

        var html = await http.GetStringAsync(SetupMode.CodePath, Cancel);
        Assert.Contains("Setup code", html, StringComparison.Ordinal);
        Assert.Contains("docker logs contactor", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_OpenTheGuide_WithTheRightCode_AndNotWithAWrongOne()
    {
        var logs = new LogCapture();
        await using var app = NewInstallation(logs);
        using var http = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using (var wrong = await SubmitAsync(http, "WRONG-CODE"))
        {
            Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
            Assert.Contains("That isn't the setup code", WebUtility.HtmlDecode(await wrong.Content.ReadAsStringAsync(Cancel)), StringComparison.Ordinal);
        }

        using (var right = await SubmitAsync(http, LoggedCode(logs)))
        {
            Assert.Equal(HttpStatusCode.Redirect, right.StatusCode);
            Assert.EndsWith("setup", right.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        using var guide = await http.GetAsync("/setup", Cancel);
        Assert.Equal(HttpStatusCode.OK, guide.StatusCode);
        Assert.Contains("How do people reach the app?", await guide.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);

        // Only the guide: the rest of the app, and what isn't a page, stay closed.
        using var settings = await http.GetAsync("/settings", Cancel);
        Assert.Equal("/setup", settings.Headers.Location?.OriginalString);
        using var backup = await http.GetAsync("/settings/backup", Cancel);
        Assert.Equal("/setup", backup.Headers.Location?.OriginalString);
    }

    // Pages other than the guide open only for someone signed in once the guide is done; a setup connection stays on
    // the guide, also after the sign-in is chosen, and the guide doesn't send it on (they would send it to and fro).
    [Fact]
    public void Should_KeepASetupConnection_OnTheGuide()
    {
        Assert.True(Contactor.Web.Components.SetupGate.Opens(isGuide: true, signedIn: false, setupDone: false));
        Assert.True(Contactor.Web.Components.SetupGate.Opens(isGuide: false, signedIn: true, setupDone: true));
        Assert.False(Contactor.Web.Components.SetupGate.Opens(isGuide: false, signedIn: false, setupDone: true));
        Assert.False(Contactor.Web.Components.SetupGate.Opens(isGuide: false, signedIn: true, setupDone: false));

        Assert.True(Contactor.Web.Components.SetupGuide.ShouldLeave(signedIn: true, part1Done: true, setupDone: true));
        Assert.False(Contactor.Web.Components.SetupGuide.ShouldLeave(signedIn: false, part1Done: true, setupDone: true));
        Assert.False(Contactor.Web.Components.SetupGuide.ShouldLeave(signedIn: true, part1Done: false, setupDone: true));
    }

    // With Cloudflare as the way in, No sign-in shows greyed out, with why.
    [Fact]
    public async Task Should_GreyOut_NoSignIn_WithCloudflare()
    {
        var logs = new LogCapture();
        await using var app = NewInstallation(logs);
        using var http = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using (await SubmitAsync(http, LoggedCode(logs)))
        {
        }

        Assert.Null(await app.Services.GetRequiredService<SetupSignInHandler>().ChooseCloudflareAsync("tunnel-token", "relays.example.com", Cancel));
        var html = WebUtility.HtmlDecode(await http.GetStringAsync("/setup", Cancel));

        Assert.Contains("How do people sign in?", html, StringComparison.Ordinal);
        var none = html[html.IndexOf("No sign-in", StringComparison.Ordinal)..];
        Assert.Contains("Only on this network.", none[..300], StringComparison.Ordinal);
        Assert.Matches("<input type=\"radio\" name=\"sign-in\"[^>]*disabled", html);
    }

    // reset-sign-in forgets the sign-in of an installation in use, and tells the running app: a new code, and the
    // guide opens again, not sent back to the relays (that went round in circles).
    [Fact]
    public async Task Should_OpenTheGuide_AfterTheSignInIsReset()
    {
        var logs = new LogCapture();
        await using var app = new TestWebApp("Production", AccessTokens.Settings, services => services.AddSingleton<ILoggerProvider>(logs));
        using var http = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using (var before = await http.GetAsync("/", Cancel))
        {
            Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);
        }

        await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContextAsync(Cancel))
        {
            await db.Installations.ExecuteUpdateAsync(set => set.SetProperty(i => i.SignInType, (string?)null), Cancel);
        }

        var codesBefore = CodePattern().Count(logs.Text);
        using (var reload = await http.PostAsync(SetupEndpoints.ReloadPath, content: null, Cancel))
        {
            Assert.Equal(HttpStatusCode.NoContent, reload.StatusCode);
        }

        // Asked again, it makes no new code: that would end the installer's cookie, and reset the wrong codes.
        var code = LoggedCode(logs);
        using (var again = await http.PostAsync(SetupEndpoints.ReloadPath, content: null, Cancel))
        {
            Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        }

        Assert.Equal(codesBefore + 1, CodePattern().Count(logs.Text));
        using (var right = await SubmitAsync(http, code))
        {
            Assert.Equal(HttpStatusCode.Redirect, right.StatusCode);
        }

        using var guide = await http.GetAsync("/setup", Cancel);
        Assert.Equal(HttpStatusCode.OK, guide.StatusCode);
        // Set up with .env, it has no address saved: the guide asks for the way in first, since it sends people there.
        Assert.Contains("How do people reach the app?", await guide.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ReloadOnlyForThisMachine()
    {
        await using var app = new TestWebApp("Production", AccessTokens.Settings) { RemoteAddress = IPAddress.Parse("192.168.1.20") };
        using var http = app.CreateClient();

        using var reload = await http.PostAsync(SetupEndpoints.ReloadPath, content: null, Cancel);

        Assert.Equal(HttpStatusCode.NotFound, reload.StatusCode);
    }

    [Fact]
    public async Task Should_NotReload_ThroughTailscaleServe()
    {
        var settings = new Dictionary<string, string?>(AccessTokens.Settings) { ["Tailscale:ServeAddress"] = "127.0.0.2:8081" };
        await using var app = new TestWebApp("Production", settings) { LocalEndPoint = IPEndPoint.Parse("127.0.0.2:8081") };
        using var http = app.CreateClient();

        using var reload = await http.PostAsync(SetupEndpoints.ReloadPath, content: null, Cancel);

        Assert.Equal(HttpStatusCode.NotFound, reload.StatusCode);
    }

    [Fact]
    public async Task Should_ForgetTheSignIn_WithTheCommand()
    {
        await using var database = TestDatabase.Create();
        var args = new[] { SignInReset.Command, $"--ConnectionStrings:Default={database.ConnectionString}" };
        using var app = new FakeApp(HttpStatusCode.NoContent);
        var missing = Reset(args, app.Url);
        Assert.Equal(1, missing.ExitCode);
        Assert.StartsWith("reset-sign-in: there is no database at ", missing.Error, StringComparison.Ordinal);

        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            db.Installations.Add(new Installation { SetupDoneUtc = DateTime.UtcNow, AccessType = "cloudflare", SignInType = "cloudflare-access" });
            await db.SaveChangesAsync(Cancel);
        }

        var forgotten = Reset(args, app.Url);
        Assert.Equal((0, ""), (forgotten.ExitCode, forgotten.Error));
        Assert.Contains("The sign-in is forgotten", forgotten.Output, StringComparison.Ordinal);
        Assert.Contains("The app wrote the new setup code to its log", forgotten.Output, StringComparison.Ordinal);
        Assert.Equal(1, app.Requests);
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            var installation = await db.Installations.SingleAsync(Cancel);
            Assert.Equal(("cloudflare", null), (installation.AccessType, installation.SignInType));
        }

        // Nothing to forget: the app isn't told again.
        var again = Reset(args, app.Url);
        Assert.Equal(0, again.ExitCode);
        Assert.StartsWith("No sign-in is chosen", again.Output, StringComparison.Ordinal);
        Assert.Equal(1, app.Requests);
    }

    [Fact]
    public async Task Should_SayWhatWentWrong_WhenTheAppAnswersAnError()
    {
        await using var database = await DatabaseInUseAsync();
        using var app = new FakeApp(HttpStatusCode.InternalServerError);

        var result = Reset([SignInReset.Command, $"--ConnectionStrings:Default={database.ConnectionString}"], app.Url);

        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("reset-sign-in: the app answered 500.", result.Error, StringComparison.Ordinal);
    }

    // The app not running: the sign-in is still forgotten, and the next start makes the code.
    [Fact]
    public async Task Should_ForgetTheSignIn_WhenTheAppIsNotRunning()
    {
        await using var database = await DatabaseInUseAsync();
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = Reset([SignInReset.Command, $"--ConnectionStrings:Default={database.ConnectionString}"], new Uri($"http://127.0.0.1:{port}/setup/reload"));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("The app isn't running", result.Output, StringComparison.Ordinal);
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        Assert.Null((await db.Installations.SingleAsync(Cancel)).SignInType);
    }

    // A database the new version hasn't brought up to date yet, e.g. before its first start.
    [Fact]
    public async Task Should_ChangeNothing_InADatabaseNotUpToDate()
    {
        await using var database = TestDatabase.Create();
        Directory.CreateDirectory(Path.GetDirectoryName(database.FilePath)!);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync(Cancel);
        }

        using var app = new FakeApp(HttpStatusCode.NoContent);
        var result = Reset([SignInReset.Command, $"--ConnectionStrings:Default={database.ConnectionString}"], app.Url);
        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("reset-sign-in: the database can't be changed yet", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, app.Requests);
    }

    // As an installation in use starts: its choice is in the database before the app starts, so the sign-in holds from
    // the first request and no setup code is made.
    [Fact]
    public async Task Should_KeepTheSignIn_OfAnInstallationInUse_FromTheStart()
    {
        var logs = new LogCapture();
        await using var app = NewInstallation(logs);
        app.SaveBeforeStart(new Installation { BoardType = "vm201", SetupDoneUtc = DateTime.UtcNow, AccessType = "cloudflare", SignInType = "cloudflare-access" });

        using var http = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await http.GetAsync("/", Cancel);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotMatch(CodePattern(), logs.Text);
        Assert.NotNull(app.Services.GetRequiredService<SignInChoice>().Current);
    }

    [Fact]
    public void Should_AskForTheCode_WhereSetupModeSendsPeople()
    {
        var route = Assert.Single(typeof(Contactor.Web.Components.SetupCodePage).GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), inherit: false));
        Assert.Equal(SetupMode.CodePath, ((Microsoft.AspNetCore.Components.RouteAttribute)route).Template);
    }

    // Every installation in use ran Cloudflare: the upgrade saves that, so only a new one opens on the setup code.
    [Fact]
    public async Task Should_SaveCloudflare_ForAnInstallationInUse_WhenUpgraded()
    {
        await using var database = TestDatabase.Create();
        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(database.FilePath)!);
            await db.GetService<IMigrator>().MigrateAsync("20261113125455_AddInstallationMethods", cancellationToken: Cancel);
            db.Installations.Add(new Installation { BoardType = "vm201", SetupDoneUtc = DateTime.UtcNow });
            await db.SaveChangesAsync(Cancel);
            await db.Database.MigrateAsync(Cancel);
        }

        await using (var db = await database.Factory.CreateDbContextAsync(Cancel))
        {
            var installation = await db.Installations.SingleAsync(Cancel);
            Assert.Equal(("cloudflare", "cloudflare-access"), (installation.AccessType, installation.SignInType));
        }
    }

    private static (SetupCode Code, LogCapture Logs) Create(TimeProvider time)
    {
        var logs = new LogCapture();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        return (new SetupCode(new EphemeralDataProtectionProvider(), time, factory.CreateLogger<SetupCode>()), logs);
    }

    // The command as it runs on the machine, keeping what it writes rather than printing it in the test run.
    private static (int ExitCode, string Output, string Error) Reset(string[] args, Uri reloadUrl)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = SignInReset.Run(args, reloadUrl, output, error);
        return (exitCode, output.ToString(), error.ToString());
    }

    private static async Task<TestDatabase> DatabaseInUseAsync()
    {
        var database = TestDatabase.Create();
        await database.Services.InitializeDatabaseAsync(relayCount: 8, Cancel);
        await using var db = await database.Factory.CreateDbContextAsync(Cancel);
        db.Installations.Add(new Installation { SetupDoneUtc = DateTime.UtcNow, AccessType = "cloudflare", SignInType = "cloudflare-access" });
        await db.SaveChangesAsync(Cancel);
        return database;
    }

    private static TestWebApp NewInstallation(LogCapture logs)
    {
        return new TestWebApp("Production", AccessTokens.Settings, services => services.AddSingleton<ILoggerProvider>(logs), setUp: false);
    }

    private static async Task<HttpResponseMessage> SubmitAsync(HttpClient http, string code)
    {
        var page = await http.GetStringAsync(SetupMode.CodePath, Cancel);
        var fields = HiddenField().Matches(page).ToDictionary(m => WebUtility.HtmlDecode(m.Groups["name"].Value), m => WebUtility.HtmlDecode(m.Groups["value"].Value));
        fields["Typed"] = code;
        using var form = new FormUrlEncodedContent(fields);
        return await http.PostAsync(SetupMode.CodePath, form, Cancel);
    }

    private static DefaultHttpContext WithCookieFrom(DefaultHttpContext answered)
    {
        var setCookie = answered.Response.Headers.SetCookie.ToString();
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = setCookie.Split(';')[0];
        return context;
    }

    private static string LoggedCode(LogCapture logs)
    {
        return CodePattern().Matches(logs.Text)[^1].Groups["code"].Value;
    }

    [GeneratedRegex("The setup code is (?<code>[A-Z0-9]{4}-[A-Z0-9]{4})")]
    private static partial Regex CodePattern();

    [GeneratedRegex("<input type=\"hidden\" name=\"(?<name>[^\"]+)\" value=\"(?<value>[^\"]*)\"")]
    private static partial Regex HiddenField();

    /// <summary>
    /// The running app as reset-sign-in sees it: answers every request with one status, and counts them. A plain socket,
    /// which needs no rights on any system (HttpListener on an address needs them on Windows).
    /// </summary>
    private sealed class FakeApp : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly HttpStatusCode _status;
        private int _requests;

        public FakeApp(HttpStatusCode status)
        {
            _status = status;
            _listener.Start();
            Url = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/setup/reload");
            _ = AnswerAsync();
        }

        public Uri Url { get; }

        public int Requests => Volatile.Read(ref _requests);

        public void Dispose()
        {
            _listener.Stop();
            _listener.Dispose();
        }

        private async Task AnswerAsync()
        {
            while (true)
            {
                System.Net.Sockets.TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                using (client)
                {
                    var stream = client.GetStream();

                    // The request's head ends with an empty line; the command sends no body.
                    var head = new System.Text.StringBuilder();
                    var buffer = new byte[1024];
                    while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(buffer);
                        if (read == 0)
                        {
                            break;
                        }

                        head.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    Interlocked.Increment(ref _requests);
                    var answer = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 {(int)_status} X\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(answer);
                }
            }
        }
    }

    /// <summary>Keeps what is logged, to read the setup code from it as a person would.</summary>
    private sealed class LogCapture : ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public string Text
        {
            get
            {
                lock (_lines)
                {
                    return string.Join('\n', _lines);
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger : ILogger
        {
            private readonly LogCapture _capture;

            public Logger(LogCapture capture)
            {
                _capture = capture;
            }

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (_capture._lines)
                {
                    _capture._lines.Add(formatter(state, exception));
                }
            }
        }
    }
}
