using Contactor.Web.Features.Access;
using Contactor.Web.Features.Identity;
using Contactor.Web.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Demo;

/// <summary>Makes a copy's database before its app first starts. A test puts one in that makes nothing.</summary>
public interface ICopyDatabase
{
    /// <summary>The connection string the copy's app is given, for its database in <paramref name="dataFolder"/>.</summary>
    string ConnectionString(string dataFolder);

    Task PrepareAsync(string dataFolder, CancellationToken cancellationToken);
}

/// <summary>
/// With the app's own database code: brings a new database up to date, and saves what the setup guide's first steps
/// would, "Only on this network" with "No sign-in". Only the demo's proxy reaches a copy, so nothing public points at
/// one directly. The guide then opens at the board, as on a new installation once its sign-in is chosen.
/// </summary>
public sealed class AppCopyDatabase : ICopyDatabase
{
    public const string FileName = "contactor.db";

    public string ConnectionString(string dataFolder)
    {
        return new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataFolder, FileName) }.ToString();
    }

    public async Task PrepareAsync(string dataFolder, CancellationToken cancellationToken)
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
        await using var provider = services.BuildServiceProvider();
        await provider.InitializeDatabaseAsync(cancellationToken);

        var factory = provider.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.Installations.Add(new Installation { AccessType = AccessCatalogue.LocalId, SignInType = SignInCatalogue.NoneId });
        await db.SaveChangesAsync(cancellationToken);
    }
}
