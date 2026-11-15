using System.Net;
using Contactor.Demo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Contactor.Tests.Demo;

/// <summary>What a copy's program gets of the demo's environment, its ports, and how the demo knows it is the program that listens.</summary>
public sealed class ProgramStarterTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("PATH", true)]
    [InlineData("HOME", true)]
    [InlineData("TMPDIR", true)]
    [InlineData("DOTNET_ROOT", true)]
    [InlineData("DOTNET_ROOT_ARM64", true)]
    [InlineData("DOTNET_gcServer", true)]
    [InlineData("DOTNET_GCHeapHardLimit", true)]
    [InlineData("DOTNET_CLI_HOME", true)]
    [InlineData("DOTNET_NOLOGO", true)]
    [InlineData("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", true)]
    [InlineData("DOTNET_ENVIRONMENT", false)]
    [InlineData("DOTNET_URLS", false)]
    [InlineData("DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE", false)]
    [InlineData("DOTNET_ConnectionStrings__Default", false)]
    [InlineData("DOTNET_Vm201__Host", false)]
    [InlineData("DOTNET_GC__Foo", false)]
    [InlineData("DOTNET_ROOT__Foo", false)]
    [InlineData("DOTNET_Kestrel", false)]
    [InlineData("ASPNETCORE_URLS", false)]
    [InlineData("ASPNETCORE_ENVIRONMENT", false)]
    [InlineData("Demo__DataRoot", false)]
    [InlineData("TUNNEL_TOKEN", false)]
    [InlineData("ConnectionStrings__Default", false)]
    [InlineData("HTTPS_PROXY", false)]
    public void Should_PassOn_OnlyWhatAProgramNeeds_OfTheDemosEnvironment(string name, bool kept)
    {
        Assert.Equal(kept, DotnetProgramStarter.Keeps(name));
    }

    [Fact]
    public void Should_TakeRandomFreePorts_BelowTheSystemsOwnRange()
    {
        var ports = new LoopbackPorts();

        var taken = Enumerable.Range(0, 10).SelectMany(_ => ports.Take(3)).ToList();

        Assert.All(taken, p => Assert.InRange(p, LoopbackPorts.First, LoopbackPorts.Last));
        Assert.All(Enumerable.Range(0, 10), i => Assert.Equal(3, taken.Skip(i * 3).Take(3).Distinct().Count()));

        // Random: thirty ports from more than ten thousand are not all in a row.
        Assert.True(taken.Distinct().Count() > 20);
    }

    [Fact]
    public async Task Should_KnowTheApp_ByItsHealthCheck()
    {
        await using var server = await ServeAsync("/healthz", "{\"boardConnected\":false,\"version\":\"1\"}");
        using var client = new HttpMessageInvoker(new SocketsHttpHandler { UseProxy = false });

        Assert.True(await ProgramProbe.App.AnswersAsync(client, server.Port, Cancel));
        Assert.False(await ProgramProbe.Simulator.AnswersAsync(client, server.Port, Cancel));
    }

    [Fact]
    public async Task Should_KnowTheSimulator_ByItsPage()
    {
        await using var server = await ServeAsync("/", "<html><head><title>Contactor simulator</title></head></html>");
        using var client = new HttpMessageInvoker(new SocketsHttpHandler { UseProxy = false });

        Assert.True(await ProgramProbe.Simulator.AnswersAsync(client, server.Port, Cancel));
        Assert.False(await ProgramProbe.App.AnswersAsync(client, server.Port, Cancel));
    }

    [Fact]
    public async Task Should_NotTakeAnythingElseOnThePort_ForTheProgram()
    {
        // E.g. a board a visitor's simulator opened on the port while the app was starting again.
        await using var server = await ServeAsync("/other", "\"boardConnected\" <title>Contactor simulator</title>");
        using var client = new HttpMessageInvoker(new SocketsHttpHandler { UseProxy = false });

        Assert.False(await ProgramProbe.App.AnswersAsync(client, server.Port, Cancel));
        Assert.False(await ProgramProbe.Simulator.AnswersAsync(client, server.Port, Cancel));
    }

    [Fact]
    public async Task Should_NotTakeAPortNothingListensOn_ForTheProgram()
    {
        var port = new LoopbackPorts().Take(1)[0];
        using var client = new HttpMessageInvoker(new SocketsHttpHandler { UseProxy = false });

        Assert.False(await ProgramProbe.App.AnswersAsync(client, port, Cancel));
    }

    private static async Task<Server> ServeAsync(string path, string body)
    {
        var port = new LoopbackPorts().Take(1)[0];
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.Run(async context =>
        {
            if (context.Request.Path != path)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync(body);
                return;
            }

            await context.Response.WriteAsync(body);
        });
        await app.StartAsync(Cancel);
        return new Server(app, port);
    }

    private sealed class Server : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public Server(WebApplication app, int port)
        {
            _app = app;
            Port = port;
        }

        public int Port { get; }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
