using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Contactor.Tests.Boards;
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

    // Still healthy while the board has no contact: the container's health is the app's, not the board's. The fields of
    // when the app ran one board stay, and each board has its own.
    [Fact]
    public async Task Should_ReportEachBoard_AndStayHealthy_WhileItHasNoContact()
    {
        await using var app = new TestWebApp();
        using var http = app.CreateClient();

        using var response = await http.GetAsync(HealthEndpoints.Path, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel));
        var root = json.RootElement;
        Assert.False(root.GetProperty("boardConnected").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("boardLastContactUtc").ValueKind);
        var board = Assert.Single(root.GetProperty("boards").EnumerateArray());
        Assert.Equal(app.Services.Live().BoardId, board.GetProperty("id").GetInt32());
        Assert.Equal("Velleman VM201", board.GetProperty("name").GetString());
        Assert.Equal("vm201", board.GetProperty("model").GetString());
        Assert.False(board.GetProperty("connected").GetBoolean());
        Assert.Equal(JsonValueKind.Null, board.GetProperty("lastContactUtc").ValueKind);
    }

    [Fact]
    public async Task Should_ReportTheBoardConnected_WithItsLastContact()
    {
        await using var app = new TestWebApp(configureServices: TestWebApp.WithBoard(new FakeRelayBoard()));
        using var http = app.CreateClient();

        using var response = await http.GetAsync(HealthEndpoints.Path, Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel));
        var root = json.RootElement;
        Assert.True(root.GetProperty("boardConnected").GetBoolean());
        Assert.Equal(DateTimeOffset.UnixEpoch, root.GetProperty("boardLastContactUtc").GetDateTimeOffset());
        var board = Assert.Single(root.GetProperty("boards").EnumerateArray());
        Assert.True(board.GetProperty("connected").GetBoolean());
        Assert.Equal(DateTimeOffset.UnixEpoch, board.GetProperty("lastContactUtc").GetDateTimeOffset());
        Assert.Equal("fake", board.GetProperty("model").GetString());
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
