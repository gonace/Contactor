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

        // Only the guide: the rest of the app, and what isn't a page, stay closed.
        using var settings = await http.GetAsync("/settings", Cancel);
        Assert.Equal("/setup", settings.Headers.Location?.OriginalString);
        using var backup = await http.GetAsync("/settings/backup", Cancel);
        Assert.Equal("/setup", backup.Headers.Location?.OriginalString);
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
