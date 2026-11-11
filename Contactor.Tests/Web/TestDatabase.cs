using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Contactor.Web.Persistence;

namespace Contactor.Tests.Web;

/// <summary>
/// A SQLite database file in a fresh temporary folder, deleted again on dispose.
/// </summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private TestDatabase(string folder, ServiceProvider services)
    {
        Folder = folder;
        Services = services;
    }

    public string Folder { get; }

    public string FilePath => Path.Combine(Folder, "data", "contactor.db");

    public string ConnectionString => $"Data Source={FilePath}";

    public ServiceProvider Services { get; }

    public IDbContextFactory<ContactorDbContext> Factory =>
        Services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();

    /// <summary>Creates the services for a database that does not exist yet (its folder neither).</summary>
    public static TestDatabase Create(Action<IServiceCollection, IConfiguration>? configure = null, IDictionary<string, string?>? settings = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "contactor-tests", Guid.NewGuid().ToString("N"));
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = $"Data Source={Path.Combine(folder, "data", "contactor.db")}",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPersistence(configuration);
        configure?.Invoke(services, configuration);
        return new TestDatabase(folder, services.BuildServiceProvider());
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Folder))
        {
            Directory.Delete(Folder, recursive: true);
        }
    }
}
