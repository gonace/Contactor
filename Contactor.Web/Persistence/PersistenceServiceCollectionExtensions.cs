using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Contactor.Web.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public const string ConnectionStringName = "Default";

    /// <summary>
    /// Registers a <see cref="ContactorDbContext"/> factory: Blazor Server components and background services each
    /// create a short-lived context per unit of work.
    /// </summary>
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"ConnectionStrings:{ConnectionStringName} is required.");
        }

        services.AddDbContextFactory<ContactorDbContext>(options => Configure(options, connectionString));

        // ASP.NET Core's keys (antiforgery, Blazor's circuit state) live next to the database: inside the container
        // they would be lost at every restart or upgrade.
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        var keys = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataSource)) ?? ".", "keys");
        services.AddDataProtection()
            .SetApplicationName("Contactor")
            .PersistKeysToFileSystem(new DirectoryInfo(keys));
        return services;
    }

    /// <summary>
    /// Brings the database up to date and seeds what must always exist: relays 1–8 and the input. Runs before the
    /// app starts serving, so every other part can rely on them. Existing rows are left as they are.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        var factory = services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var dataSource = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory) && dataSource != ":memory:")
        {
            Directory.CreateDirectory(directory);
        }

        await db.Database.MigrateAsync(cancellationToken);

        // Kept in the database file, so setting it once is enough; readers then never block the writer.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);

        var existing = await db.Relays.Select(r => r.Id).ToListAsync(cancellationToken);
        for (var id = 1; id <= Relay.Count; id++)
        {
            if (!existing.Contains(id))
            {
                db.Relays.Add(new Relay
                {
                    Id = id,
                    Name = $"Relä {id}",
                    IsActive = true,
                    MaxOnMinutes = Relay.DefaultMaxOnMinutes,
                    SortOrder = id,
                });
            }
        }

        // One runtime row per relay from the start, so two writers never both try to insert the first one.
        var runtimes = await db.RelayRuntimes.Select(r => r.RelayId).ToListAsync(cancellationToken);
        for (var id = 1; id <= Relay.Count; id++)
        {
            if (!runtimes.Contains(id))
            {
                db.RelayRuntimes.Add(new RelayRuntime { RelayId = id });
            }
        }

        if (!await db.Inputs.AnyAsync(cancellationToken))
        {
            db.Inputs.Add(new Input { Name = "Ingång", ClosedText = "Stängd", OpenText = "Öppen" });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    internal static void Configure(DbContextOptionsBuilder options, string connectionString)
    {
        options.UseSqlite(connectionString).AddInterceptors(BusyTimeoutInterceptor.Instance);
    }

    /// <summary>
    /// Sets <c>busy_timeout</c> on every connection: a write waits up to 5 s for another writer instead of failing.
    /// </summary>
    private sealed class BusyTimeoutInterceptor : DbConnectionInterceptor
    {
        public static BusyTimeoutInterceptor Instance { get; } = new();

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            ArgumentNullException.ThrowIfNull(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout=5000;";
            command.ExecuteNonQuery();
        }

        public override async Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(connection);
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "PRAGMA busy_timeout=5000;";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// Lets <c>dotnet ef</c> create migrations without starting the app.
/// </summary>
public sealed class ContactorDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ContactorDbContext>
{
    public ContactorDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ContactorDbContext>();
        PersistenceServiceCollectionExtensions.Configure(options, "Data Source=design-time.db");
        return new ContactorDbContext(options.Options);
    }
}
