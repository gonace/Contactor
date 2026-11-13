namespace Contactor.AppHost;

/// <summary>
/// Runs everything for development: <c>dotnet run --project Contactor.AppHost</c>.
/// </summary>
public static class Program
{
    // The real board's port. Not proxied, so stopping the simulator in the dashboard closes the connection just as a
    // board that loses power would.
    private const int Vm201Port = 9760;

    public static async Task Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        // The simulator's page is its http endpoint, linked from the dashboard; the board itself is its own port.
        var simulator = builder.AddProject<Projects.Contactor_Simulator>("simulator")
            .WithEnvironment("Simulator__Boards__0__Type", "vm201")
            .WithEndpoint(port: Vm201Port, scheme: "tcp", name: "vm201", env: "Simulator__Boards__0__Port", isProxied: false)
            .WithHttpHealthCheck("/health");

        // Development settings come from the web app's appsettings.Development.json (a local database file); this
        // points it at the simulator.
        var web = builder.AddProject<Projects.Contactor_Web>("web")
            .WithEnvironment("Vm201__Host", "127.0.0.1")
            .WithEnvironment("Vm201__Port", Vm201Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithHttpHealthCheck("/health")
            .WaitFor(simulator);

        // The app runs cloudflared itself for the Cloudflare way in, as it does in the image. Without one it starts
        // anyway: only that way in fails, saying so in the app's log.
        var cloudflared = await GetCloudflaredAsync(builder.AppHostDirectory);
        if (cloudflared is not null)
        {
            web.WithEnvironment("Tunnel__Path", cloudflared);
        }

        await builder.Build().RunAsync();
    }

    // In .tools/ next to this project (ignored by git), so it outlives a clean build and nothing else on the computer is
    // touched.
    private static async Task<string?> GetCloudflaredAsync(string appHostDirectory)
    {
        var folder = Path.Combine(appHostDirectory, ".tools", "cloudflared", Cloudflared.Version);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        try
        {
            return await Cloudflared.GetAsync(Cloudflared.HostSystem(), folder, http, CancellationToken.None);
        }
        catch (Exception exception) when (exception is CloudflaredUnavailableException or HttpRequestException
                                              or IOException or UnauthorizedAccessException or TaskCanceledException
                                              or InvalidDataException or FormatException)
        {
            await Console.Error.WriteLineAsync($"warn: No cloudflared {Cloudflared.Version}, so the Cloudflare way in won't work: {exception.Message}");
            return null;
        }
    }
}
