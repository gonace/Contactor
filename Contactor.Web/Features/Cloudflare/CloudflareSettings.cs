using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Contactor.Web.Features.Cloudflare;

/// <summary>Cloudflare Access's settings: whose tokens the app takes.</summary>
/// <param name="TeamDomain">The team domain, e.g. <c>myteam.cloudflareaccess.com</c>; a host name only.</param>
/// <param name="Audience">The Access application's AUD tag.</param>
public sealed record CloudflareAccessSettings(string TeamDomain, string Audience)
{
    /// <summary>The issuer Access puts in its tokens.</summary>
    public string Issuer => $"https://{TeamDomain}";

    /// <summary>Where Access publishes its signing keys.</summary>
    public Uri CertificatesUri => new($"https://{TeamDomain}/cdn-cgi/access/certs");

    /// <summary>What is wrong with them, in plain words, or null when they can be used.</summary>
    public string? Problem => CloudflareSettings.CheckAccess(TeamDomain, Audience);
}

/// <summary>The tunnel's settings: its token, and the address people open.</summary>
public sealed record CloudflareTunnelSettings(string Token, string? Address);

/// <summary>
/// Cloudflare's settings while the app runs: what the setup guide saved (<see cref="CloudflareSetting"/>), and for
/// what it didn't, the app's settings (<c>Tunnel:Token</c>, <c>CloudflareAccess:TeamDomain</c>,
/// <c>CloudflareAccess:Audience</c>), as before. A change takes effect at once: the tunnel starts again, and the
/// token check uses the new team and application.
/// </summary>
public sealed partial class CloudflareSettings : IDisposable
{
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly TunnelOptions _tunnelOptions;
    private readonly CloudflareAccessOptions _accessOptions;
    private readonly ILogger<CloudflareSettings> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CloudflareTunnelSettings _tunnel;
    private CloudflareAccessSettings _access;

    public CloudflareSettings(
        IDbContextFactory<ContactorDbContext> dbFactory,
        IOptions<TunnelOptions> tunnelOptions,
        IOptions<CloudflareAccessOptions> accessOptions,
        ILogger<CloudflareSettings> logger)
    {
        ArgumentNullException.ThrowIfNull(tunnelOptions);
        ArgumentNullException.ThrowIfNull(accessOptions);
        _dbFactory = dbFactory;
        _tunnelOptions = tunnelOptions.Value;
        _accessOptions = accessOptions.Value;
        _logger = logger;
        _tunnel = Combine(null);
        _access = CombineAccess(null);
    }

    /// <summary>The tunnel's token changed: the tunnel starts again with it.</summary>
    public event EventHandler? TunnelChanged;

    public CloudflareTunnelSettings Tunnel => Volatile.Read(ref _tunnel);

    public CloudflareAccessSettings Access => Volatile.Read(ref _access);

    /// <summary>At start: reads what the setup guide saved.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.CloudflareSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        Volatile.Write(ref _tunnel, Combine(row));
        Volatile.Write(ref _access, CombineAccess(row));
    }

    /// <summary>
    /// Saves the tunnel's token and the address people open, and starts the tunnel again with them. Returns what is
    /// wrong in plain words, and saves nothing then.
    /// </summary>
    public async Task<string?> SaveTunnelAsync(string? token, string? address, CancellationToken cancellationToken)
    {
        token = token?.Trim() ?? "";
        address = NormalizeHost(address);
        var problem = CheckTunnel(token, address);
        if (problem is not null)
        {
            return problem;
        }

        await SaveAsync(row => (row.TunnelToken, row.Address) = (token, address), cancellationToken);
        LogTunnelSaved(address);
        TunnelChanged?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>
    /// Saves Cloudflare Access's team domain and AUD tag; the next request is checked against them. Returns what is
    /// wrong in plain words, and saves nothing then.
    /// </summary>
    public async Task<string?> SaveAccessAsync(string? teamDomain, string? audience, CancellationToken cancellationToken)
    {
        teamDomain = NormalizeHost(teamDomain);
        audience = audience?.Trim() ?? "";
        var problem = CheckAccess(teamDomain, audience);
        if (problem is not null)
        {
            return problem;
        }

        await SaveAsync(row => (row.TeamDomain, row.Audience) = (teamDomain, audience), cancellationToken);
        LogAccessSaved(teamDomain);
        return null;
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <summary>What is wrong with Cloudflare Access's settings, in plain words, or null.</summary>
    public static string? CheckAccess(string? teamDomain, string? audience)
    {
        if (string.IsNullOrEmpty(teamDomain) || Uri.CheckHostName(teamDomain) != UriHostNameType.Dns || teamDomain.Length > CloudflareSetting.HostNameMaxLength)
        {
            return "The team domain must be a host name, e.g. myteam.cloudflareaccess.com.";
        }

        if (string.IsNullOrEmpty(audience) || audience.Length > CloudflareSetting.AudienceMaxLength || audience.Any(char.IsWhiteSpace))
        {
            return "The AUD tag is the long code under the Access application's Additional settings.";
        }

        return null;
    }

    /// <summary>What is wrong with the tunnel's settings, in plain words, or null.</summary>
    public static string? CheckTunnel(string token, string? address)
    {
        if (token.Length == 0 || token.Length > CloudflareSetting.TunnelTokenMaxLength || token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return "The tunnel token is the long code after --token in the tunnel's install command.";
        }

        if (string.IsNullOrEmpty(address) || Uri.CheckHostName(address) != UriHostNameType.Dns || !address.Contains('.', StringComparison.Ordinal))
        {
            return "The address must be the tunnel's public hostname, e.g. relays.example.com.";
        }

        return null;
    }

    // A host name as typed or pasted: "https://Relays.Example.com/" is relays.example.com.
    private static string NormalizeHost(string? value)
    {
        var host = value?.Trim() ?? "";
        if (Uri.TryCreate(host, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            host = uri.Host;
        }

        return host.TrimEnd('/').ToLowerInvariant();
    }

    private async Task SaveAsync(Action<CloudflareSetting> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var row = await db.CloudflareSettings.SingleOrDefaultAsync(cancellationToken);
            if (row is null)
            {
                row = new CloudflareSetting();
                db.CloudflareSettings.Add(row);
            }

            change(row);
            await db.SaveChangesAsync(cancellationToken);
            Volatile.Write(ref _tunnel, Combine(row));
            Volatile.Write(ref _access, CombineAccess(row));
        }
        finally
        {
            _gate.Release();
        }
    }

    private CloudflareTunnelSettings Combine(CloudflareSetting? row)
    {
        return new CloudflareTunnelSettings(row?.TunnelToken ?? _tunnelOptions.Token.Trim(), row?.Address);
    }

    private CloudflareAccessSettings CombineAccess(CloudflareSetting? row)
    {
        return new CloudflareAccessSettings(row?.TeamDomain ?? _accessOptions.TeamDomain, row?.Audience ?? _accessOptions.Audience);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cloudflare tunnel saved for {Address}")]
    private partial void LogTunnelSaved(string address);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cloudflare Access saved for the team {TeamDomain}")]
    private partial void LogAccessSaved(string teamDomain);
}
