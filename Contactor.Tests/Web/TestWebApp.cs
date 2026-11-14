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
using Microsoft.Extensions.Logging;
using Contactor.Web;
using Contactor.Web.Features.Access;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Cloudflare;
using Contactor.Web.Features.Identity;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>
/// The whole web app in memory with its own database file, a board port where nothing listens, and every request
/// coming from <see cref="RemoteAddress"/> (the in-memory test server has no address of its own). Unless asked for a
/// new installation, it has been through the setup guide with the board <c>Board:Type</c> names (the VM201 when not
/// set), behind Cloudflare, so the app opens on the relay list.
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

    /// <summary>
    /// The setting, for this test app only, of who a Development app's requests are signed in as (John when not set).
    /// It stands in for Cloudflare Access: the app itself has no such setting.
    /// </summary>
    public const string SignedInAs = "Test:SignedInAs";

    public IPAddress? RemoteAddress { get; set; } = IPAddress.Loopback;

    /// <summary>The connection's own end, as the address and port a request came in on; unset when null.</summary>
    public IPEndPoint? LocalEndPoint { get; set; }

    /// <summary>
    /// The services for a board that answered once and has lost contact since, so the screens show its last known
    /// state with a warning; without it the board never answers and every state is unknown.
    /// </summary>
    public static void WithLostBoard(IServiceCollection services)
    {
        WithBoard(FakeRelayBoard.LostContact())(services);
    }

    /// <summary>
    /// The services for an app whose board is <paramref name="board"/>, in place of the one it chose, once it is set
    /// up.
    /// </summary>
    public static Action<IServiceCollection> WithBoard(IRelayBoard board)
    {
        return services => services.AddSingleton(new TestBoard(board));
    }

    public string DatabasePath => Path.Combine(_folder, "contactor.db");

    /// <summary>
    /// Saves <paramref name="installation"/> in a database made before the app starts, as an installation in use has
    /// it when the app starts. Use with <c>setUp: false</c>, before the first request.
    /// </summary>
    public void SaveBeforeStart(Installation installation, string? boardType = null)
    {
        Directory.CreateDirectory(_folder);
        var options = new DbContextOptionsBuilder<ContactorDbContext>().UseSqlite($"Data Source={DatabasePath}").Options;
        using var db = new ContactorDbContext(options);
        db.Database.Migrate();
        db.Installations.Add(installation);
        if (boardType is not null)
        {
            db.Boards.Add(new Board { Type = boardType, SortOrder = 1 });
        }

        db.SaveChanges();
    }

    /// <summary>
    /// Adds the board's relays and inputs in <paramref name="layout"/> to a database made before the app starts, so the
    /// app's seeding finds them. Before the first request.
    /// </summary>
    public void UseLayoutBeforeStart(RelayLayout layout, int relayCount = 8, int inputCount = 1)
    {
        Directory.CreateDirectory(_folder);
        var options = new DbContextOptionsBuilder<ContactorDbContext>().UseSqlite($"Data Source={DatabasePath}").Options;
        using var db = new ContactorDbContext(options);
        db.Database.Migrate();
        RelayLayouts.SeedAsync(db, layout, relayCount, inputCount).GetAwaiter().GetResult();
    }

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

        // The app's log goes nowhere, so the test run prints only its own lines. A test that reads the log adds its own
        // provider below.
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter>(new RemoteAddressStartupFilter(this));

            // In Development, Cloudflare Access's place says who everyone is, as the test asks. Production tests sign
            // in with real tokens.
            if (_environment == "Development")
            {
                var email = _settings.TryGetValue(SignedInAs, out var name) && name is not null ? name : "john@example.com";
                services.AddKeyedSingleton<ISignInMethod>(CloudflareMethods.AccessId, new FixedSignIn(email));
            }

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
            db.Installations.Add(new Installation
            {
                SetupDoneUtc = DateTime.UtcNow,
                AccessType = CloudflareMethods.TunnelId,
                SignInType = CloudflareMethods.AccessId,
            });
            db.SaveChanges();

            // As at the start after an upgrade: an installation in use runs Board:Type's board, or the VM201, behind
            // Cloudflare, as every installation in use did.
            host.Services.GetRequiredService<BoardChoice>().ApplySavedAsync(CancellationToken.None).GetAwaiter().GetResult();
            host.Services.GetRequiredService<AccessChoice>().ApplySavedAsync(CancellationToken.None).GetAwaiter().GetResult();
            host.Services.GetRequiredService<SignInChoice>().ApplySavedAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (host.Services.GetService<TestBoard>() is { } test)
            {
                host.Services.UseFakeBoardAsync(test.Board).GetAwaiter().GetResult();
            }
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

    // The board a test runs in the app's place (WithBoard).
    private sealed record TestBoard(IRelayBoard Board);

    // Signs every request in as one person, in Cloudflare Access's place.
    private sealed class FixedSignIn : ISignInMethod
    {
        private readonly string _email;

        public FixedSignIn(string email)
        {
            _email = email;
        }

        public Task<string?> AuthenticateAsync(HttpContext context) => Task.FromResult<string?>(_email);
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
                    if (_app.LocalEndPoint is { } local)
                    {
                        context.Connection.LocalIpAddress = local.Address;
                        context.Connection.LocalPort = local.Port;
                    }

                    return nextMiddleware(context);
                });
                next(builder);
            };
        }
    }
}
