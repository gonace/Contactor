namespace Contactor.AppHost;

/// <summary>
/// Runs everything for development: <c>dotnet run --project Contactor.AppHost</c>.
/// </summary>
public static class Program
{
    // The real board's port. Not proxied, so stopping the simulator in the dashboard closes the connection just as a
    // board that loses power would.
    private const int Vm201Port = 9760;

    public static void Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        // The simulator's page is its http endpoint, linked from the dashboard; the board itself is its own port.
        var simulator = builder.AddProject<Projects.Contactor_Simulator>("simulator")
            .WithEnvironment("Simulator__Boards__0__Type", "vm201")
            .WithEndpoint(port: Vm201Port, scheme: "tcp", name: "vm201", env: "Simulator__Boards__0__Port", isProxied: false)
            .WithHttpHealthCheck("/health");

        // Development settings come from the web app's appsettings.Development.json (a local database file); this
        // points it at the simulator.
        builder.AddProject<Projects.Contactor_Web>("web")
            .WithEnvironment("Vm201__Host", "127.0.0.1")
            .WithEnvironment("Vm201__Port", Vm201Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithHttpHealthCheck("/health")
            .WaitFor(simulator);

        builder.Build().Run();
    }
}
