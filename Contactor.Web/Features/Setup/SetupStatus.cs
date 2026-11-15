using Contactor.Web.Features.Access;
using Contactor.Web.Features.Cloudflare;
using Contactor.Web.Features.Tailscale;
using Contactor.Web.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Contactor.Web.Features.Setup;

/// <summary>
/// <c>Contactor.Web setup-status</c>, run on the machine by the install script (<c>docker exec contactor dotnet
/// /app/Contactor.Web.dll setup-status</c>): says how far the setup is, from the database, so the script knows whether
/// to show the setup code. It writes one word, and the address people open on a second line when the app knows it:
/// <list type="bullet">
/// <item><c>setup-code</c>: no sign-in is chosen; the setup guide opens behind the setup code in the app's log.</item>
/// <item><c>setup-guide</c>: a sign-in is chosen, but the setup guide isn't finished.</item>
/// <item><c>done</c>: set up.</item>
/// </list>
/// It changes nothing, and never writes the setup code, which only the app's log has.
/// </summary>
public static class SetupStatus
{
    public const string Command = "setup-status";
    public const string Code = "setup-code";
    public const string Guide = "setup-guide";
    public const string Done = "done";

    private static readonly TimeSpan TailscaleTimeout = TimeSpan.FromSeconds(5);

    public static int Run(string[] args)
    {
        return Run(args, tailscale: null, Console.Out, Console.Error);
    }

    /// <summary>
    /// As <see cref="Run(string[])"/>, asking <paramref name="tailscale"/> (the image's when null) for the app's address
    /// on the Tailscale network, and writing to <paramref name="output"/> and its errors to <paramref name="error"/>.
    /// </summary>
    public static int Run(string[] args, ITailscaleProcess? tailscale, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        var builder = WebApplication.CreateBuilder(args.Where(a => a != Command).ToArray());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPersistence(builder.Configuration);
        using var provider = services.BuildServiceProvider();
        var location = provider.GetRequiredService<DatabaseLocation>();
        if (!File.Exists(location.Path))
        {
            error.WriteLine($"setup-status: there is no database at {location.Path}.");
            return 1;
        }

        Installation? installation;
        CloudflareSetting? cloudflare;
        try
        {
            using var db = provider.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContext();
            installation = db.Installations.AsNoTracking().SingleOrDefault();
            cloudflare = db.CloudflareSettings.AsNoTracking().SingleOrDefault();
        }
        catch (SqliteException ex)
        {
            error.WriteLine($"setup-status: the database can't be read yet ({ex.Message}).");
            return 1;
        }

        output.WriteLine(installation switch
        {
            null or { SignInType: null } => Code,
            { SetupDoneUtc: null } => Guide,
            _ => Done,
        });

        var address = installation?.AccessType switch
        {
            CloudflareMethods.TunnelId when !string.IsNullOrEmpty(cloudflare?.Address) => cloudflare.Address,
            TailscaleMethods.NetworkId => TailscaleAddress(tailscale ?? Image(builder.Configuration.GetSection(TailscaleOptions.SectionName))),
            _ => null,
        };
        if (address is not null)
        {
            output.WriteLine($"https://{address}");
        }

        return 0;
    }

    // The app's name on the Tailscale network, as tailscaled says it, or null while it doesn't know one.
    private static string? TailscaleAddress(ITailscaleProcess tailscale)
    {
        try
        {
            var result = tailscale.RunAsync(["status", "--json"], TailscaleTimeout, CancellationToken.None).GetAwaiter().GetResult();
            return result.Succeeded ? TailscaleNetwork.ParseState(result.Output)?.Address : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            // No tailscale to run, e.g. outside the image.
            return null;
        }
    }

    // The image's tailscale, as the app runs it.
    private static TailscaleProcess Image(IConfigurationSection section)
    {
        var options = new TailscaleOptions();
        section.Bind(options);
        return new TailscaleProcess(Options.Create(options), TimeProvider.System);
    }
}
