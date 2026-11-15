using System.Collections.Concurrent;
using System.Net;
using Contactor.Web.Features.Health;
using Contactor.Web.Features.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Contactor.Tests.Web;

/// <summary>
/// With ASP.NET Core's forwarded headers on, anyone could write a made-up address in <c>X-Forwarded-For</c> and pass
/// "Only on this network": the app refuses to start.
/// </summary>
public sealed class ForwardedHeadersCheckTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ForwardedHeaders_Enabled is the configuration key; FORWARDEDHEADERS_ENABLED is the key that the environment
    // variable ASPNETCORE_FORWARDEDHEADERS_ENABLED becomes. Keys are case-insensitive. The test sets no real
    // environment variable, as other tests run in the same process.
    [Theory]
    [InlineData("ForwardedHeaders_Enabled", "true")]
    [InlineData("ForwardedHeaders_Enabled", "True")]
    [InlineData("FORWARDEDHEADERS_ENABLED", "true")]
    [InlineData("FORWARDEDHEADERS_ENABLED", "TRUE")]
    public async Task Should_RefuseToStart_WithForwardedHeadersOn(string key, string value)
    {
        var logs = new ErrorCollector();
        await using var app = new TestWebApp(
            settings: new Dictionary<string, string?> { [key] = value },
            configureServices: services => services.AddSingleton<ILoggerProvider>(logs),
            setUp: false);

        var error = Assert.Throws<InvalidOperationException>(() => app.CreateClient());

        Assert.Equal(ForwardedHeadersCheck.Message, error.Message);
        Assert.Equal([ForwardedHeadersCheck.Message], logs.Errors);
    }

    // As ASP.NET Core reads it: only "true" turns the forwarded headers on.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("0")]
    public async Task Should_Start_WithForwardedHeadersOff(string? value)
    {
        var settings = new Dictionary<string, string?>();
        if (value is not null)
        {
            settings[ForwardedHeadersCheck.Key] = value;
        }

        await using var app = new TestWebApp(settings: settings);
        using var http = app.CreateClient();

        using var response = await http.GetAsync(HealthEndpoints.Path, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The errors logged, as they read.</summary>
    private sealed class ErrorCollector : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<string> _errors = new();

        public IReadOnlyList<string> Errors => [.. _errors];

        public ILogger CreateLogger(string categoryName)
        {
            return this;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= LogLevel.Error;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                _errors.Enqueue(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }
    }
}
