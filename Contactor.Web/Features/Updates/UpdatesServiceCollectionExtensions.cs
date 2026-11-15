using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Contactor.Web.Features.Updates;

public static class UpdatesServiceCollectionExtensions
{
    /// <summary>"Update now" in Settings: the folder shared with the update helper, GitHub's releases and the service that follows both.</summary>
    public static IServiceCollection AddUpdates(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<UpdateOptions>()
            .Bind(configuration.GetSection(UpdateOptions.SectionName))
            .Validate(
                o => string.IsNullOrEmpty(o.ReleasesUrl) || Uri.TryCreate(o.ReleasesUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps,
                "Updates:ReleasesUrl must be an https address, or empty for none.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(AppBuild.Current);
        services.AddSingleton<UpdateFolder>();
        services.AddHttpClient(GitHubReleases.HttpClientName);
        services.TryAddSingleton<IReleaseSource, GitHubReleases>();
        services.AddSingleton<UpdateService>();
        services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());
        return services;
    }
}
