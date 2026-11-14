namespace Contactor.Web.Features.Health;

/// <summary>
/// <c>Contactor.Web healthcheck [url]</c>: asks the running app's <c>/healthz</c> and exits 0 when it answers
/// 200, otherwise 1. For the container health check, since the image has no shell or curl. A board that is not
/// connected still counts as healthy: restarting the app wouldn't bring it back.
/// </summary>
public static class HealthProbe
{
    public const string Command = "healthcheck";
    public const string DefaultUrl = "http://127.0.0.1:8080" + HealthEndpoints.Path;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static int Run(string url)
    {
        return Run(url, Console.Error);
    }

    /// <summary>As <see cref="Run(string)"/>, writing what went wrong to <paramref name="error"/>.</summary>
    public static int Run(string url, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)
        {
            error.WriteLine($"healthcheck: {url} is not an http address on this machine.");
            return 1;
        }

        try
        {
            using var http = new HttpClient { Timeout = Timeout };
            using var response = http.GetAsync(uri).GetAwaiter().GetResult();
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            error.WriteLine($"healthcheck: {ex.Message}");
            return 1;
        }
    }
}
