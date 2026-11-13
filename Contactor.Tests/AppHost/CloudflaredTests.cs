using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Contactor.AppHost;

namespace Contactor.Tests.AppHost;

public sealed class CloudflaredTests : IDisposable
{
    private static readonly byte[] Program = Encoding.ASCII.GetBytes("the cloudflared program");
    private static readonly string ProgramSha256 = Convert.ToHexStringLower(SHA256.HashData(Program));

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "contactor-cloudflared-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task Should_DownloadAndCheckTheProgram_WhenItIsMissing()
    {
        var server = new FakeRelease(Program);
        var path = Path.Combine(_folder, "cloudflared");

        var got = await Cloudflared.GetAsync(new Cloudflared.Build("cloudflared-linux-amd64", ProgramSha256), path, server.Client, TestContext.Current.CancellationToken);

        Assert.Equal(path, got);
        Assert.Equal(Program, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal("https://github.com/cloudflare/cloudflared/releases/download/2026.10.7/cloudflared-linux-amd64", Assert.Single(server.Requests).ToString());
        Assert.Equal([Path.GetFileName(path)], Directory.GetFiles(_folder).Select(Path.GetFileName));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public async Task Should_UseTheProgramAlreadyDownloaded_WithoutDownloadingAgain()
    {
        var server = new FakeRelease(Program);
        var path = Path.Combine(_folder, "cloudflared");
        Directory.CreateDirectory(_folder);
        await File.WriteAllBytesAsync(path, Program, TestContext.Current.CancellationToken);

        await Cloudflared.GetAsync(new Cloudflared.Build("cloudflared-linux-amd64", ProgramSha256), path, server.Client, TestContext.Current.CancellationToken);

        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Should_DownloadAgain_WhenTheProgramThereHasBeenChanged()
    {
        var server = new FakeRelease(Program);
        var path = Path.Combine(_folder, "cloudflared");
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(path, "changed", TestContext.Current.CancellationToken);

        await Cloudflared.GetAsync(new Cloudflared.Build("cloudflared-linux-amd64", ProgramSha256), path, server.Client, TestContext.Current.CancellationToken);

        Assert.Single(server.Requests);
        Assert.Equal(Program, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Should_RejectAndDeleteADownload_ThatDoesNotMatchItsChecksum()
    {
        var server = new FakeRelease(Encoding.ASCII.GetBytes("something else"));
        var path = Path.Combine(_folder, "cloudflared");

        await Assert.ThrowsAsync<CloudflaredUnavailableException>(() =>
            Cloudflared.GetAsync(new Cloudflared.Build("cloudflared-linux-amd64", ProgramSha256), path, server.Client, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Should_TakeTheProgramOutOfATgz()
    {
        var server = new FakeRelease(Tgz(("README.md", Encoding.ASCII.GetBytes("readme")), ("cloudflared", Program)));
        var path = Path.Combine(_folder, "cloudflared");

        await Cloudflared.GetAsync(new Cloudflared.Build("cloudflared-darwin-arm64.tgz", ProgramSha256), path, server.Client, TestContext.Current.CancellationToken);

        Assert.Equal(Program, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal([Path.GetFileName(path)], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Should_RejectATgz_WithoutTheProgram()
    {
        var server = new FakeRelease(Tgz(("README.md", Encoding.ASCII.GetBytes("readme"))));
        var path = Path.Combine(_folder, "cloudflared");

        await Assert.ThrowsAsync<CloudflaredUnavailableException>(() =>
            Cloudflared.GetAsync(new Cloudflared.Build("cloudflared-darwin-arm64.tgz", ProgramSha256), path, server.Client, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Should_LeaveNothingBehind_WhenTheDownloadFails()
    {
        var server = new FakeRelease(Program, HttpStatusCode.NotFound);
        var path = Path.Combine(_folder, "cloudflared");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Cloudflared.GetAsync(new Cloudflared.Build("cloudflared-linux-amd64", ProgramSha256), path, server.Client, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Should_SaySo_ForASystemWithoutABuild()
    {
        var server = new FakeRelease(Program);

        var error = await Assert.ThrowsAsync<CloudflaredUnavailableException>(() =>
            Cloudflared.GetAsync("linux-riscv64", _folder, server.Client, TestContext.Current.CancellationToken));

        Assert.Contains("linux-riscv64", error.Message, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData("linux-x64", "cloudflared-linux-amd64")]
    [InlineData("linux-arm64", "cloudflared-linux-arm64")]
    [InlineData("win-x64", "cloudflared-windows-amd64.exe")]
    [InlineData("win-arm64", "cloudflared-windows-amd64.exe")]
    [InlineData("osx-x64", "cloudflared-darwin-amd64.tgz")]
    [InlineData("osx-arm64", "cloudflared-darwin-arm64.tgz")]
    public void Should_HaveABuildWithAChecksum_ForEverySystem(string system, string asset)
    {
        var build = Cloudflared.Builds[system];

        Assert.Equal(asset, build.Asset);
        Assert.Matches("^[0-9a-f]{64}$", build.Sha256);
    }

    [Fact]
    public async Task Should_DownloadTheWindowsBuild_OnWindows()
    {
        var server = new FakeRelease(Encoding.ASCII.GetBytes("not the real program"));

        await Assert.ThrowsAsync<CloudflaredUnavailableException>(() =>
            Cloudflared.GetAsync("win-x64", _folder, server.Client, TestContext.Current.CancellationToken));

        Assert.EndsWith("/cloudflared-windows-amd64.exe", Assert.Single(server.Requests).ToString(), StringComparison.Ordinal);
    }

    private static byte[] Tgz(params (string Name, byte[] Data)[] files)
    {
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            foreach (var (name, data) in files)
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
            }
        }

        return memory.ToArray();
    }

    // Cloudflare's release on GitHub: answers every request with the same file.
    private sealed class FakeRelease : HttpMessageHandler
    {
        private readonly byte[] _file;
        private readonly HttpStatusCode _status;

        public FakeRelease(byte[] file, HttpStatusCode status = HttpStatusCode.OK)
        {
            _file = file;
            _status = status;
            Client = new HttpClient(this);
        }

        public HttpClient Client { get; }

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new ByteArrayContent(_file) });
        }
    }
}
