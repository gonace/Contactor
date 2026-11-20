namespace Contactor.AppHost;

/// <summary>
/// Runs everything for development: <c>aspire run</c>. The app runs as its image, built
/// from the <c>Dockerfile</c> with cloudflared and Tailscale inside, as on a real installation; it needs Docker Desktop.
/// The public demo runs beside it as <c>contactor-demo</c>, with copies of its own.
/// </summary>
public static class Program
{
    // The real board's port. Not proxied, so stopping the simulator in the dashboard closes the connection just as a
    // board that loses power would.
    private const int Vm201Port = 9760;

    // The port the image listens on, published as the same port on this computer.
    private const int AppPort = 8080;

    // Where a container reaches this computer, as Docker Desktop names it. The app's Vm201__Host is the name Aspire gets
    // from the container runtime, which is this one on Docker Desktop, the runtime the AppHost needs. Another runtime
    // can use another name (Podman: host.containers.internal), and the simulator's page then shows this one.
    private const string ContainerHost = "host.docker.internal";

    // The build the demo starts its copies of the app and the simulator from: the AppHost's own, which builds them
    // through its reference to the demo.
#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    public static void Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        // The simulator's page is its http endpoint, linked from the dashboard; the board itself is its own port. The app
        // runs in a container and reaches the simulator on this computer at host.docker.internal, so the page says so.
        // That connection comes in on this computer's network interface, not loopback, so every board listens on all
        // addresses (Windows' firewall may ask once whether to allow the simulator).
        var simulator = builder.AddDotnetProject("simulator",
                Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "Contactor.Simulator", "Contactor.Simulator.csproj")))
            .WithEnvironment("Simulator__AppAddress", ContainerHost)
            .WithEnvironment("Simulator__ListenOnAllAddresses", "true")
            .WithEnvironment("Simulator__Boards__0__Type", "vm201")
            .WithEndpoint(port: Vm201Port, scheme: "tcp", name: "vm201", env: "Simulator__Boards__0__Port", isProxied: false)
            .WithHttpHealthCheck("/health");

        // The image, run as on a real installation: Production, everything it keeps in /data (a Docker volume, so the
        // database and Tailscale's state survive a restart; delete the volume to start over). Aspire gives it the
        // simulator's address as a container reaches it, and sends its telemetry to the dashboard. Docker Desktop shows
        // the container under "contactor", as "contactor-web", the name of its image too. The image is always tagged
        // "dev": Aspire otherwise gives each build a new tag, so every run left one more image of about 445 MB behind.
        // A new build takes the tag from the one before, which stays as an untagged image: `docker image prune` removes
        // those, and every other untagged image, but not one a container still uses (e.g. a container a crashed run
        // left behind: remove the container first). Images from older runs have a hash as their tag
        // (contactor-web:<hash>, older ones web:<hash>), so prune keeps them: `docker images contactor-web` and
        // `docker images web` list them, and `docker image rm` removes them.
        var board = simulator.GetEndpoint("vm201");
        builder.AddDockerfile("contactor-web", Path.Combine(builder.AppHostDirectory, ".."))
            .WithImageTag("dev")
            .WithHttpEndpoint(port: AppPort, targetPort: AppPort, name: "http", isProxied: false)
            .WithVolume("contactor-dev", "/data")
            .WithEnvironment("Vm201__Host", board.Property(EndpointProperty.Host))
            .WithEnvironment("Vm201__Port", board.Property(EndpointProperty.Port))
            .WithOtlpExporter()
            .InDockerProject()
            .WaitFor(simulator);

        // The public demo (docs/DEMO.md) on http://localhost:5070: each visitor gets their own copy of the app and the
        // simulator, started from their build output, not the resources above. Aspire runs it with `dotnet run
        // --no-launch-profile`, and its launch profile has only its address; this is the one place its settings are
        // given in development, with full paths.
        var root = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, ".."));
        string Output(string project) => Path.Combine(root, project, "bin", Configuration, "net10.0", project + ".dll");
        builder.AddDotnetProject("contactor-demo", Path.Combine(root, "Contactor.Demo", "Contactor.Demo.csproj"))
            .WithEnvironment("Demo__AppPath", Output("Contactor.Web"))
            .WithEnvironment("Demo__SimulatorPath", Output("Contactor.Simulator"))
            .WithEnvironment("Demo__CopyEnvironment", "Development")
            .WithEnvironment("Demo__DataRoot", Path.Combine(root, "Contactor.Demo", "bin", "demo-data"))
            .WithHttpHealthCheck("/healthz");

        builder.Build().Run();
    }
}
