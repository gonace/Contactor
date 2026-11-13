using System.Data.Common;
using Contactor.Boards;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Contactor.Web.Persistence;

/// <summary>Where the database is, and whether it is still in the folder images before 2026-11 used.</summary>
public sealed record DatabaseLocation(string Path, bool IsEarlierFolder);

public static class PersistenceServiceCollectionExtensions
{
    public const string ConnectionStringName = "Default";

    /// <summary>The image keeps everything in /data.</summary>
    public const string ImagePath = "/data/contactor.db";

    /// <summary>Where images before 2026-11 kept the database, and an installation's old compose.yml still maps it.</summary>
    public const string EarlierPath = "/var/lib/contactor/contactor.db";

    /// <summary>
    /// The connection string to use. An installation whose compose.yml predates the /data folder maps its data to
    /// <see cref="EarlierPath"/>, and the image's default would start it on an empty database: so while there is no
    /// database at <see cref="ImagePath"/> and there is one at the earlier path, that one is used (and the app says to
    /// run install.sh, which writes the new compose.yml). Remove once every installation has run it.
    /// </summary>
    public static (string ConnectionString, DatabaseLocation Location) Resolve(string connectionString, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (builder.DataSource == ImagePath && !fileExists(ImagePath) && fileExists(EarlierPath))
        {
            builder.DataSource = EarlierPath;
            return (builder.ToString(), new DatabaseLocation(EarlierPath, IsEarlierFolder: true));
        }

        return (connectionString, new DatabaseLocation(builder.DataSource, IsEarlierFolder: false));
    }

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

        (connectionString, var location) = Resolve(connectionString, File.Exists);
        services.AddSingleton(location);
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
    /// Brings the database up to date and seeds what must always exist: a row for each of the board's relays and
    /// inputs. Runs before the app starts serving, so every other part can rely on them. Before a board is chosen there
    /// is nothing to seed; <c>BoardChoice</c> seeds the board's rows once one is.
    /// </summary>
    public static Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var board = services.GetRequiredService<IRelayBoard>();
        return services.InitializeDatabaseAsync(board.Capabilities.RelayCount, board.Capabilities.InputCount, cancellationToken);
    }

    /// <summary>As the overload below, for a board with one input.</summary>
    public static Task InitializeDatabaseAsync(this IServiceProvider services, int relayCount, CancellationToken cancellationToken)
    {
        return services.InitializeDatabaseAsync(relayCount, inputCount: 1, cancellationToken);
    }

    /// <summary>
    /// As <see cref="InitializeDatabaseAsync(IServiceProvider, CancellationToken)"/>, for relays 1 to
    /// <paramref name="relayCount"/> and inputs 1 to <paramref name="inputCount"/> (<see cref="SeedBoardAsync"/>).
    /// </summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, int relayCount, int inputCount, CancellationToken cancellationToken)
    {
        CheckCounts(relayCount, inputCount);
        var factory = services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(cancellationToken))
        {
            var dataSource = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
            var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
            if (!string.IsNullOrEmpty(directory) && dataSource != ":memory:")
            {
                Directory.CreateDirectory(directory);
            }

            await db.Database.MigrateAsync(cancellationToken);

            // Kept in the database file, so setting it once is enough; readers then never block the writer.
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        }

        await services.SeedBoardAsync(relayCount, inputCount, cancellationToken);
    }

    /// <summary>
    /// Adds the rows a board with <paramref name="relayCount"/> relays and <paramref name="inputCount"/> inputs needs:
    /// a relay, its runtime row and an input for each, numbered from 1. Existing rows are left as they are, also
    /// relays and inputs above the count, which a board with more left behind: their events still point to them.
    /// </summary>
    public static async Task SeedBoardAsync(this IServiceProvider services, int relayCount, int inputCount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        CheckCounts(relayCount, inputCount);
        var factory = services.GetRequiredService<IDbContextFactory<ContactorDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Relays.Select(r => r.Id).ToListAsync(cancellationToken);
        for (var id = 1; id <= relayCount; id++)
        {
            if (!existing.Contains(id))
            {
                db.Relays.Add(new Relay
                {
                    Id = id,
                    Name = Relay.DefaultName(id),
                    IsActive = true,
                    MaxOnMinutes = Relay.DefaultMaxOnMinutes,
                    SortOrder = id,
                });
            }
        }

        // One runtime row per relay from the start, so two writers never both try to insert the first one.
        var runtimes = await db.RelayRuntimes.Select(r => r.RelayId).ToListAsync(cancellationToken);
        for (var id = 1; id <= relayCount; id++)
        {
            if (!runtimes.Contains(id))
            {
                db.RelayRuntimes.Add(new RelayRuntime { RelayId = id });
            }
        }

        var inputs = await db.Inputs.Select(i => i.Id).ToListAsync(cancellationToken);
        for (var id = 1; id <= inputCount; id++)
        {
            if (!inputs.Contains(id))
            {
                db.Inputs.Add(new Input { Id = id, Name = Input.DefaultName(id) });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static void CheckCounts(int relayCount, int inputCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(relayCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(relayCount, ChannelMask.MaxChannels);
        ArgumentOutOfRangeException.ThrowIfNegative(inputCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(inputCount, ChannelMask.MaxChannels);
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
