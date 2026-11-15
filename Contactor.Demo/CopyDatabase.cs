using Contactor.Web.Features.Access;
using Contactor.Web.Features.Identity;
using Contactor.Web.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Contactor.Demo;

/// <summary>Makes a copy's database before its app first starts. A test puts one in that makes nothing.</summary>
public interface ICopyDatabase
{
    /// <summary>The connection string the copy's app is given, for its database in <paramref name="dataFolder"/>.</summary>
    string ConnectionString(string dataFolder);

    Task PrepareAsync(string dataFolder, CancellationToken cancellationToken);

    /// <summary>
    /// "Skip setup": fills the copy's database with the Workshop template, while its app doesn't run. False, and nothing
    /// changed, when the database isn't as a new copy has it any more (the visitor got further in the setup guide).
    /// </summary>
    Task<bool> ApplyTemplateAsync(string dataFolder, CancellationToken cancellationToken);
}

/// <summary>
/// With the app's own database code: brings a new database up to date, and saves what the setup guide's first steps
/// would, "Only on this network" with "No sign-in". Only the demo's proxy reaches a copy, so nothing public points at
/// one directly. The guide then opens at the board, as on a new installation once its sign-in is chosen. On request it
/// fills it with <see cref="WorkshopTemplate"/> instead.
/// </summary>
public sealed class AppCopyDatabase : ICopyDatabase
{
    public const string FileName = "contactor.db";

    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;

    public AppCopyDatabase(TimeProvider time, IOptions<DemoOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _time = time;
        _zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);
    }

    public string ConnectionString(string dataFolder)
    {
        return new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataFolder, FileName) }.ToString();
    }

    public async Task PrepareAsync(string dataFolder, CancellationToken cancellationToken)
    {
        await using var provider = Services(dataFolder);
        await provider.InitializeDatabaseAsync(cancellationToken);

        var factory = provider.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.Installations.Add(new Installation { AccessType = AccessCatalogue.LocalId, SignInType = SignInCatalogue.NoneId });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ApplyTemplateAsync(string dataFolder, CancellationToken cancellationToken)
    {
        await using var provider = Services(dataFolder);
        var factory = provider.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(cancellationToken))
        {
            if (!await WorkshopTemplate.IsFreshAsync(db, cancellationToken))
            {
                return false;
            }
        }

        await WorkshopTemplate.FillAsync(provider, _zone, _time.GetUtcNow().UtcDateTime, cancellationToken);
        return true;
    }

    // The app's database services for the copy's database.
    private ServiceProvider Services(string dataFolder)
    {
        // No pooled connection may keep the file open once the copy is deleted.
        var connectionString = new SqliteConnectionStringBuilder(ConnectionString(dataFolder)) { Pooling = false }.ToString();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{PersistenceServiceCollectionExtensions.ConnectionStringName}"] = connectionString,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPersistence(configuration);
        return services.BuildServiceProvider();
    }
}
