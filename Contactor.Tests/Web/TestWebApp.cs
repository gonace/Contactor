using System.Net;
using Contactor.Boards;
using Contactor.Tests.Boards;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Contactor.Web;
using Contactor.Web.Features.Boards;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>
/// The whole web app in memory with its own database file, a board port where nothing listens, and every request
/// coming from <see cref="RemoteAddress"/> (the in-memory test server has no address of its own). Unless asked for a
/// new installation, it has been through the setup guide with the board <c>Board:Type</c> names (the VM201 when not
/// set), so the app opens on the relay list.
/// </summary>
internal sealed class TestWebApp : WebApplicationFactory<Program>
{
    private readonly string _environment;
    private readonly IReadOnlyDictionary<string, string?> _settings;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly bool _setUp;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "contactor-tests", Guid.NewGuid().ToString("N"));

    public TestWebApp(
        string environment = "Development",
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null,
        bool setUp = true)
    {
        _environment = environment;
        _setUp = setUp;
        _settings = settings ?? new Dictionary<string, string?>();
        _configureServices = configureServices;
    }

    public IPAddress? RemoteAddress { get; set; } = IPAddress.Loopback;

    /// <summary>
    /// The services for a board that answered once and has lost contact since, so the screens show its last known
    /// state with a warning; without it the board never answers and every state is unknown.
    /// </summary>
    public static void WithLostBoard(IServiceCollection services)
    {
        services.AddSingleton<IRelayBoard>(FakeRelayBoard.LostContact());
    }

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

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        if (_setUp)
        {
            var factory = host.Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
            using var db = factory.CreateDbContext();
            db.Installations.Add(new Installation { SetupDoneUtc = DateTime.UtcNow });
            db.SaveChanges();

            // As at the start after an upgrade: an installation in use runs Board:Type's board, or the VM201.
            host.Services.GetRequiredService<BoardChoice>().ApplySavedAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        return host;
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
