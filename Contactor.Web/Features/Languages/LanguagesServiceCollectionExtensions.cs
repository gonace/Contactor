using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Contactor.Web.Features.Languages;

public static class LanguagesServiceCollectionExtensions
{
    /// <summary>
    /// The UI's texts (resource files in <c>Resources/</c>, read through <c>IStringLocalizer</c>) and the app's one
    /// language, which every request takes.
    /// </summary>
    public static IServiceCollection AddLanguages(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddLocalization();
        services.TryAddSingleton(LanguageCatalogue.Default);
        services.AddSingleton<AppLanguage>();
        services.AddOptions<RequestLocalizationOptions>().Configure<LanguageCatalogue, AppLanguage>((options, catalogue, language) =>
        {
            var cultures = catalogue.All.Select(l => l.Culture).ToList();
            options.SupportedCultures = cultures;
            options.SupportedUICultures = cultures;
            options.DefaultRequestCulture = new Microsoft.AspNetCore.Localization.RequestCulture(catalogue.Fallback.Code);
            options.RequestCultureProviders = [new AppLanguageCultureProvider(language)];
        });
        return services;
    }
}
