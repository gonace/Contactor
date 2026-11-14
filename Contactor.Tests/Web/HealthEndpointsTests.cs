using System.Net;
using System.Net.Http.Json;
using Contactor.Web.Features.Health;

namespace Contactor.Tests.Web;

public sealed class HealthEndpointsTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Should_ReportBoardConnection_WhenAskedFromThisMachine()
    {
        await using var app = new TestWebApp();
        using var http = app.CreateClient();

        var health = await http.GetFromJsonAsync<HealthResponse>(HealthEndpoints.Path, Cancel);

        Assert.NotNull(health);
        Assert.False(health.BoardConnected);
        Assert.Null(health.BoardLastContactUtc);
        Assert.Equal("0.0.0-dev", health.Version);
    }

    [Fact]
    public async Task Should_Answer404_WhenAskedFromAnotherMachine()
    {
        await using var app = new TestWebApp { RemoteAddress = IPAddress.Parse("192.0.2.10") };
        using var http = app.CreateClient();

        using var response = await http.GetAsync(HealthEndpoints.Path, Cancel);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Should_Answer404_WhenTheCallerIsUnknown()
    {
        await using var app = new TestWebApp { RemoteAddress = null };
        using var http = app.CreateClient();

        using var response = await http.GetAsync(HealthEndpoints.Path, Cancel);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Serve's proxy connects from 127.0.0.1 too, but passes on requests from anyone on the Tailscale network.
    [Fact]
    public async Task Should_Answer404_ThroughTailscaleServe()
    {
        await using var app = new TestWebApp(settings: new Dictionary<string, string?> { ["Tailscale:ServeAddress"] = "127.0.0.2:8081" })
        {
            LocalEndPoint = IPEndPoint.Parse("127.0.0.2:8081"),
        };
        using var http = app.CreateClient();

        using var response = await http.GetAsync(HealthEndpoints.Path, Cancel);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Should_SeedTheDatabase_WhenTheAppStarts()
    {
        await using var app = new TestWebApp();
        using var http = app.CreateClient();

        using var response = await http.GetAsync("/", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(File.Exists(app.DatabasePath));
    }
}
