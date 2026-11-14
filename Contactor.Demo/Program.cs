namespace Contactor.Demo;

/// <summary>
/// The public demo: each visitor gets a copy of their own, the app and the simulator, each its own process on
/// 127.0.0.1, reached only through this proxy.
/// </summary>
public sealed class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Each request through the proxy would be a line otherwise.
        builder.Logging.AddFilter("Yarp", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Services.AddDemo(builder.Configuration);

        var app = builder.Build();
        app.MapDemo();
        app.Run();
    }
}
