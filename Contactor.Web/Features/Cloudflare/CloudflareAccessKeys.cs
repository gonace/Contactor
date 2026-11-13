using Microsoft.IdentityModel.Tokens;

namespace Contactor.Web.Features.Cloudflare;

/// <summary>
/// The keys Cloudflare Access signs its tokens with, fetched from the team's certs endpoint and kept for an hour.
/// A token signed with a key we don't have triggers a refetch (Access rotates keys), at most once a minute. A new team
/// domain (<see cref="CloudflareSettings"/>) starts over: the old team's keys are dropped and the new team's fetched.
/// </summary>
public sealed partial class CloudflareAccessKeys : IDisposable
{
    public const string HttpClientName = "cloudflare-access";

    public static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
    public static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(1);

    // The certs endpoint returns a few keys; anything this big is not it.
    private const int MaxResponseBytes = 64 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CloudflareSettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CloudflareAccessKeys> _logger;
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private IReadOnlyList<SecurityKey> _keys = [];
    private DateTimeOffset? _fetchedAt;

    // The team the keys are for; only read and written under the fetch gate or by a reader that checks it.
    private string? _fetchedFor;

    public CloudflareAccessKeys(
        IHttpClientFactory httpClientFactory,
        CloudflareSettings settings,
        TimeProvider timeProvider,
        ILogger<CloudflareAccessKeys> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// The current keys; empty until a fetch has succeeded, so every token fails. With <paramref name="refresh"/>
    /// they are fetched again unless that happened less than a minute ago.
    /// </summary>
    public async Task<IReadOnlyList<SecurityKey>> GetAsync(CloudflareAccessSettings access, bool refresh, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (!NeedsFetch(access, refresh))
        {
            return Volatile.Read(ref _keys);
        }

        await _fetchGate.WaitAsync(cancellationToken);
        try
        {
            // Another request may have fetched them while this one waited.
            // Not on the visitor's request: if their phone drops it, the fetch must still finish, or everyone is
            // denied until the next one is allowed a minute later. The HTTP client's own timeout bounds it.
            if (NeedsFetch(access, refresh))
            {
                await FetchAsync(access, CancellationToken.None);
            }

            return Volatile.Read(ref _keys);
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    public void Dispose()
    {
        _fetchGate.Dispose();
    }

    private bool NeedsFetch(CloudflareAccessSettings access, bool refresh)
    {
        var fetchedAt = _fetchedAt;
        if (fetchedAt is null || !string.Equals(Volatile.Read(ref _fetchedFor), access.TeamDomain, StringComparison.Ordinal))
        {
            return true;
        }

        var age = _timeProvider.GetUtcNow() - fetchedAt.Value;
        return age >= CacheDuration || (refresh && age >= MinRefreshInterval);
    }

    private async Task FetchAsync(CloudflareAccessSettings access, CancellationToken cancellationToken)
    {
        // Another team's keys are no use: they go, whether or not the new team's come.
        if (!string.Equals(_fetchedFor, access.TeamDomain, StringComparison.Ordinal))
        {
            Volatile.Write(ref _keys, []);
            Volatile.Write(ref _fetchedFor, access.TeamDomain);
        }

        // Counts as a fetch even when it fails, so a Cloudflare outage doesn't get a request per page view.
        _fetchedAt = _timeProvider.GetUtcNow();
        try
        {
            using var http = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await http.GetAsync(access.CertificatesUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                throw new InvalidDataException("The Access certs response is too large.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            var buffer = new char[MaxResponseBytes + 1];
            var read = await reader.ReadBlockAsync(buffer, cancellationToken);
            if (read > MaxResponseBytes)
            {
                throw new InvalidDataException("The Access certs response is too large.");
            }

            var keys = new JsonWebKeySet(new string(buffer, 0, read)).GetSigningKeys();
            Volatile.Write(ref _keys, [.. keys]);
            LogFetched(keys.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A failed fetch must not crash the request; it is logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Keeps the keys it had: they are still Cloudflare's. Without any, every token is denied, and the
            // first unknown key retries after a minute.
            LogFetchFailed(ex.GetType().Name, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Fetched {KeyCount} Cloudflare Access signing keys")]
    private partial void LogFetched(int keyCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not fetch the Cloudflare Access signing keys: {ErrorType}: {Reason}")]
    private partial void LogFetchFailed(string errorType, string reason);
}
