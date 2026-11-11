using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Contactor.Web;

namespace Contactor.Tests.Web;

/// <summary>
/// The whole web app in memory with its own database file, a board port where nothing listens, and every request
/// coming from <see cref="RemoteAddress"/> (the in-memory test server has no address of its own).
/// </summary>
internal sealed class TestWebApp : WebApplicationFactory<Program>
{
    private readonly string _environment;
    private readonly IReadOnlyDictionary<string, string?> _settings;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "contactor-tests", Guid.NewGuid().ToString("N"));

    public TestWebApp(
        string environment = "Development",
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null)
    {
        _environment = environment;
        _settings = settings ?? new Dictionary<string, string?>();
        _configureServices = configureServices;
    }

    public IPAddress? RemoteAddress { get; set; } = IPAddress.Loopback;

    public string DatabasePath => Path.Combine(_folder, "contactor.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={DatabasePath}");
        builder.UseSetting("Vm201:Host", "127.0.0.1");
        builder.UseSetting("Vm201:Port", "1");
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter>(new RemoteAddressStartupFilter(this));
            _configureServices?.Invoke(services);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class RemoteAddressStartupFilter : IStartupFilter
    {
        private readonly TestWebApp _app;

        public RemoteAddressStartupFilter(TestWebApp app)
        {
            _app = app;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return builder =>
            {
                builder.Use((HttpContext context, RequestDelegate nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = _app.RemoteAddress;
                    return nextMiddleware(context);
                });
                next(builder);
            };
        }
    }
}
