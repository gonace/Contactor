using System.Net;
using System.Net.Sockets;
using System.Text;
using Contactor.Web.Features.Health;

namespace Contactor.Tests.Web;

public sealed class HealthProbeTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("HTTP/1.1 200 OK", 0)]
    [InlineData("HTTP/1.1 404 Not Found", 1)]
    [InlineData("HTTP/1.1 503 Service Unavailable", 1)]
    public async Task Should_ExitByTheAnswer(string statusLine, int exitCode)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = AnswerOnceAsync(listener, statusLine);

        var result = await Task.Run(() => HealthProbe.Run($"http://127.0.0.1:{port}/healthz", TextWriter.Null), Cancel);

        await server;
        Assert.Equal(exitCode, result);
    }

    [Fact]
    public void Should_Fail_WhenNothingListens()
    {
        using var error = new StringWriter();

        Assert.Equal(1, HealthProbe.Run("http://127.0.0.1:1/healthz", error));
        Assert.StartsWith("healthcheck: ", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("https://127.0.0.1:8080/healthz")]
    [InlineData("http://example.com/healthz")]
    public void Should_OnlyAskThisMachineOverHttp(string url)
    {
        using var error = new StringWriter();

        Assert.Equal(1, HealthProbe.Run(url, error));
        Assert.Equal($"healthcheck: {url} is not an http address on this machine.{Environment.NewLine}", error.ToString());
    }

    private static async Task AnswerOnceAsync(TcpListener listener, string statusLine)
    {
        using var client = await listener.AcceptTcpClientAsync(Cancel);
        var stream = client.GetStream();
        var buffer = new byte[4096];
        _ = await stream.ReadAsync(buffer, Cancel);
        var response = Encoding.ASCII.GetBytes($"{statusLine}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, Cancel);
    }
}
