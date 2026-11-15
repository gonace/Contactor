using Microsoft.AspNetCore.Localization;

namespace Contactor.Web.Features.Languages;

/// <summary>
/// The only way a request gets its culture: the app's one language (<see cref="AppLanguage"/>). Not the browser's
/// Accept-Language, a cookie or a person's setting. A page's live connection starts with its request's culture.
/// </summary>
public sealed class AppLanguageCultureProvider : RequestCultureProvider
{
    private readonly AppLanguage _language;

    public AppLanguageCultureProvider(AppLanguage language)
    {
        _language = language;
    }

    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        var code = _language.Current.Code;
        return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(code, code));
    }
}
