using Contactor.Web.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Setup;

/// <summary>
/// <c>Contactor.Web reset-sign-in</c>, run on the machine (<c>docker exec contactor dotnet Contactor.Web.dll
/// reset-sign-in</c>): forgets how people sign in, so the setup guide opens again behind a new setup code. It opens at
/// "How do people sign in?". For a Cloudflare installation without a saved tunnel address (e.g. one set up with
/// install.sh), it opens at "How do people reach the app?". The way in (so the tunnel keeps running), the board, the
/// relays, the events and the schedules stay. The way back in for someone locked out, e.g. by a sign-in that doesn't
/// work yet.
/// </summary>
public static class SignInReset
{
    public const string Command = "reset-sign-in";
    public const string DefaultUrl = "http://127.0.0.1:8080" + SetupEndpoints.ReloadPath;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static int Run(string[] args)
    {
        return Run(args, new Uri(DefaultUrl), Console.Out, Console.Error);
    }

    /// <summary>
    /// As <see cref="Run(string[])"/>, telling the app at <paramref name="reloadUrl"/> and writing what it says to
    /// <paramref name="output"/> and its errors to <paramref name="error"/>.
    /// </summary>
    public static int Run(string[] args, Uri reloadUrl, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(reloadUrl);
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
            error.WriteLine($"reset-sign-in: there is no database at {location.Path}.");
            return 1;
        }

        int changed;
        try
        {
            using var db = provider.GetRequiredService<IDbContextFactory<ContactorDbContext>>().CreateDbContext();
            changed = db.Installations
                .Where(i => i.SignInType != null)
                .ExecuteUpdate(set => set.SetProperty(i => i.SignInType, (string?)null));
        }
        catch (SqliteException ex)
        {
            error.WriteLine($"reset-sign-in: the database can't be changed yet ({ex.Message}). Start the app once, then try again.");
            return 1;
        }

        if (changed == 0)
        {
            output.WriteLine("No sign-in is chosen: the setup guide already opens behind the setup code in the app's log.");
            return 0;
        }

        output.WriteLine("The sign-in is forgotten: the setup guide opens again, behind a new setup code.");
        try
        {
            using var http = new HttpClient { Timeout = Timeout };
            using var response = http.PostAsync(reloadUrl, content: null).GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode)
            {
                output.WriteLine("The app wrote the new setup code to its log, e.g. docker logs contactor.");
                return 0;
            }

            error.WriteLine($"reset-sign-in: the app answered {(int)response.StatusCode}. Restart it: it writes a new setup code to its log when it starts.");
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            output.WriteLine("The app isn't running: it writes the setup code to its log when it starts.");
            return 0;
        }
    }
}
