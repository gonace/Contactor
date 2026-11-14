namespace Contactor.AppHost;

/// <summary>
/// Runs everything for development: <c>dotnet run --project Contactor.AppHost</c>. The app runs as its image, built
/// from the <c>Dockerfile</c> with cloudflared and Tailscale inside, as on a real installation; it needs Docker Desktop.
/// </summary>
public static class Program
{
    // The real board's port. Not proxied, so stopping the simulator in the dashboard closes the connection just as a
    // board that loses power would.
    private const int Vm201Port = 9760;

    // The port the image listens on, published as the same port on this computer.
    private const int AppPort = 8080;

    public static void Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        // The simulator's page is its http endpoint, linked from the dashboard; the board itself is its own port.
        var simulator = builder.AddProject<Projects.Contactor_Simulator>("simulator")
            .WithEnvironment("Simulator__Boards__0__Type", "vm201")
            .WithEndpoint(port: Vm201Port, scheme: "tcp", name: "vm201", env: "Simulator__Boards__0__Port", isProxied: false)
            .WithHttpHealthCheck("/health");

        // The image, run as on a real installation: Production, everything it keeps in /data (a Docker volume, so the
        // database and Tailscale's state survive a restart; delete the volume to start over). Aspire gives it the
        // simulator's address as a container reaches it, and sends its telemetry to the dashboard.
        var board = simulator.GetEndpoint("vm201");
        builder.AddDockerfile("web", Path.Combine(builder.AppHostDirectory, ".."))
            .WithHttpEndpoint(port: AppPort, targetPort: AppPort, name: "http", isProxied: false)
            .WithVolume("contactor-dev", "/data")
            .WithEnvironment("Vm201__Host", board.Property(EndpointProperty.Host))
            .WithEnvironment("Vm201__Port", board.Property(EndpointProperty.Port))
            .WithOtlpExporter()
            .WaitFor(simulator);

        builder.Build().Run();
    }
}
