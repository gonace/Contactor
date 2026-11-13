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

        var got = await Cloudflared.GetAsync(new Cloudflared.Build("cloudflared-linux-amd64", ProgramSha256), path, server.Client, null, TestContext.Current.CancellationToken);

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

        await Cloudflared.GetAsync(new Cloudflared.Build("cloudflared-linux-amd64", ProgramSha256), path, server.Client, null, TestContext.Current.CancellationToken);

        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Should_DownloadAgain_WhenTheProgramThereHasBeenChanged()
    {
        var server = new FakeRelease(Program);
        var path = Path.Combine(_folder, "cloudflared");
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(path, "changed", TestContext.Current.CancellationToken);

        await Cloudflared.GetAsync(new Cloudflared.Build("cloudflared-linux-amd64", ProgramSha256), path, server.Client, null, TestContext.Current.CancellationToken);

        Assert.Single(server.Requests);
        Assert.Equal(Program, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Should_RejectAndDeleteADownload_ThatDoesNotMatchItsChecksum()
    {
        var server = new FakeRelease(Encoding.ASCII.GetBytes("something else"));
        var path = Path.Combine(_folder, "cloudflared");

        await Assert.ThrowsAsync<CloudflaredUnavailableException>(() =>
            Cloudflared.GetAsync(new Cloudflared.Build("cloudflared-linux-amd64", ProgramSha256), path, server.Client, null, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Should_TakeTheProgramOutOfATgz()
    {
        var server = new FakeRelease(Tgz(("README.md", Encoding.ASCII.GetBytes("readme")), ("cloudflared", Program)));
        var path = Path.Combine(_folder, "cloudflared");

        await Cloudflared.GetAsync(new Cloudflared.Build("cloudflared-darwin-arm64.tgz", ProgramSha256), path, server.Client, null, TestContext.Current.CancellationToken);

        Assert.Equal(Program, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal([Path.GetFileName(path)], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Should_RejectATgz_WithoutTheProgram()
    {
        var server = new FakeRelease(Tgz(("README.md", Encoding.ASCII.GetBytes("readme"))));
        var path = Path.Combine(_folder, "cloudflared");

        await Assert.ThrowsAsync<CloudflaredUnavailableException>(() =>
            Cloudflared.GetAsync(new Cloudflared.Build("cloudflared-darwin-arm64.tgz", ProgramSha256), path, server.Client, null, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Should_LeaveNothingBehind_WhenTheDownloadFails()
    {
        var server = new FakeRelease(Program, HttpStatusCode.NotFound);
        var path = Path.Combine(_folder, "cloudflared");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Cloudflared.GetAsync(new Cloudflared.Build("cloudflared-linux-amd64", ProgramSha256), path, server.Client, null, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Should_SaySo_ForASystemWithoutABuild()
    {
        var server = new FakeRelease(Program);

        var error = await Assert.ThrowsAsync<CloudflaredUnavailableException>(() =>
            Cloudflared.GetAsync("linux-riscv64", _folder, server.Client, null, TestContext.Current.CancellationToken));

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
    public async Task Should_AskForTheWindowsBuild_ForWindows()
    {
        var server = new FakeRelease(Encoding.ASCII.GetBytes("not the real program"));

        await Assert.ThrowsAsync<CloudflaredUnavailableException>(() =>
            Cloudflared.GetAsync("win-x64", _folder, server.Client, null, TestContext.Current.CancellationToken));

        Assert.EndsWith("/cloudflared-windows-amd64.exe", Assert.Single(server.Requests).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_SayItIsDownloading_OnlyWhenItDownloads()
    {
        var server = new FakeRelease(Program);
        var path = Path.Combine(_folder, "cloudflared");
        var said = 0;

        await Cloudflared.GetAsync(new Cloudflared.Build("cloudflared-linux-amd64", ProgramSha256), path, server.Client, () => said++, TestContext.Current.CancellationToken);
        await Cloudflared.GetAsync(new Cloudflared.Build("cloudflared-linux-amd64", ProgramSha256), path, server.Client, () => said++, TestContext.Current.CancellationToken);

        Assert.Equal(1, said);
    }

    [Fact]
    public async Task Should_StopADownloadThatStalls_WhenCancelled()
    {
        var body = new EndlessStream();
        var server = new FakeRelease(body);
        var path = Path.Combine(_folder, "cloudflared");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var download = Cloudflared.GetAsync(new Cloudflared.Build("cloudflared-linux-amd64", ProgramSha256), path, server.Client, null, cancel.Token);
        await body.Reading.WaitAsync(TestContext.Current.CancellationToken);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Should_RejectADownload_ThatSaysItIsTooLarge()
    {
        var server = new FakeRelease(Program, contentLength: Cloudflared.MaxDownloadBytes + 1);
        var path = Path.Combine(_folder, "cloudflared");

        await Assert.ThrowsAsync<CloudflaredUnavailableException>(() =>
            Cloudflared.GetAsync(new Cloudflared.Build("cloudflared-linux-amd64", ProgramSha256), path, server.Client, null, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Should_StopADownload_ThatGrowsTooLarge()
    {
        var server = new FakeRelease(new EndlessStream(delay: false));
        var path = Path.Combine(_folder, "cloudflared");

        await Assert.ThrowsAsync<CloudflaredUnavailableException>(() =>
            Cloudflared.GetAsync(new Cloudflared.Build("cloudflared-linux-amd64", ProgramSha256), path, server.Client, null, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Theory]
    [InlineData("win-x64", "cloudflared.exe")]
    [InlineData("win-arm64", "cloudflared.exe")]
    [InlineData("linux-x64", "cloudflared")]
    [InlineData("osx-arm64", "cloudflared")]
    public void Should_NameTheProgram_AsTheSystemRunsIt(string system, string name)
    {
        Assert.Equal(name, Cloudflared.ProgramName(system));
    }

    [Fact]
    public void Should_RemoveOtherVersions_ButKeepThisOne()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "2025.1.0"));
        File.WriteAllText(Path.Combine(_folder, "2025.1.0", "cloudflared"), "old");
        Directory.CreateDirectory(Path.Combine(_folder, Cloudflared.Version));

        Cloudflared.RemoveOtherVersions(_folder);
        Cloudflared.RemoveOtherVersions(Path.Combine(_folder, "missing"));

        Assert.Equal([Cloudflared.Version], Directory.GetDirectories(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public void Should_BeTheVersionTheImageHas()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Contactor.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var dockerfile = File.ReadAllText(Path.Combine(root.FullName, "Dockerfile"));
        Assert.Contains($"FROM cloudflare/cloudflared:{Cloudflared.Version}@", dockerfile, StringComparison.Ordinal);
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
        private readonly Func<HttpContent> _content;
        private readonly HttpStatusCode _status;

        public FakeRelease(byte[] file, HttpStatusCode status = HttpStatusCode.OK, long? contentLength = null)
            : this(() =>
            {
                var content = new ByteArrayContent(file);
                content.Headers.ContentLength = contentLength ?? file.Length;
                return content;
            }, status)
        {
        }

        // A body without a length, read from the stream as it comes.
        public FakeRelease(Stream body)
            : this(() => new StreamContent(body), HttpStatusCode.OK)
        {
        }

        private FakeRelease(Func<HttpContent> content, HttpStatusCode status)
        {
            _content = content;
            _status = status;
            Client = new HttpClient(this);
        }

        public HttpClient Client { get; }

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(_status) { Content = _content() });
        }
    }

    // A body that never ends: with delay, it stalls until cancelled, as a download whose connection hangs does;
    // without, it gives bytes for ever.
    private sealed class EndlessStream : Stream
    {
        private readonly bool _delay;
        private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EndlessStream(bool delay = true)
        {
            _delay = delay;
        }

        // Set once the body is being read, so the headers are in.
        public Task Reading => _reading.Task;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _reading.TrySetResult();
            if (_delay)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            buffer.Span.Fill(1);
            return buffer.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
