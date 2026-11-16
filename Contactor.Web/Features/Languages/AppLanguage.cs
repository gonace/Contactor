using System.Globalization;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Languages;

/// <summary>
/// The app's one language, for everyone: the one saved with the installation (<see cref="Installation.Language"/>), or
/// the catalogue's default until one is chosen. Every request and every page's live connection use it
/// (<see cref="AppLanguageCultureProvider"/>); a change reloads every open page (<c>LanguageWatch</c>).
/// </summary>
public sealed partial class AppLanguage : IDisposable
{
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly LanguageCatalogue _catalogue;
    private readonly ILogger<AppLanguage> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Language _current;

    public AppLanguage(IDbContextFactory<ContactorDbContext> dbFactory, LanguageCatalogue catalogue, ILogger<AppLanguage> log)
    {
        _dbFactory = dbFactory;
        _catalogue = catalogue;
        _log = log;
        _current = catalogue.Fallback;
    }

    /// <summary>Another language was chosen: open pages reload in it.</summary>
    public event EventHandler? Changed;

    /// <summary>The language the app is in now.</summary>
    public Language Current => Volatile.Read(ref _current);

    /// <summary>The languages to choose from.</summary>
    public IReadOnlyList<Language> All => _catalogue.All;

    /// <summary>Whether there is anything to choose; the choices show only then.</summary>
    public bool HasChoice => _catalogue.HasChoice;

    /// <summary>
    /// At start: reads the saved language. One this version doesn't have (a language taken out) is logged, and the app
    /// goes on in the default: the relays matter more than the words.
    /// </summary>
    public async Task ApplySavedAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var saved = await db.Installations.Select(i => i.Language).SingleOrDefaultAsync(cancellationToken);
        var language = _catalogue.Find(saved);
        if (saved is not null && language is null)
        {
            LogUnknown(saved, _catalogue.Fallback.Code);
        }

        Volatile.Write(ref _current, language ?? _catalogue.Fallback);
    }

    /// <summary>
    /// The choice in Settings or the setup guide: saves it and uses it from now on, for everyone. False for a code the
    /// catalogue doesn't have; nothing is saved then. One choice at a time (two pages), so what is saved is what is used.
    /// </summary>
    public async Task<bool> ChooseAsync(string code, CancellationToken cancellationToken)
    {
        if (_catalogue.Find(code) is not { } language)
        {
            return false;
        }

        bool changed;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SaveAsync(language.Code, cancellationToken);
            changed = Interlocked.Exchange(ref _current, language).Code != language.Code;
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            LogChosen(language.Code);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    /// <summary>
    /// The app's language on this flow until disposed. For a page's work for a change raised on another thread, e.g. a
    /// board's new state: <c>InvokeAsync</c> runs it in that thread's culture, not the page's, and the page would be
    /// drawn in English.
    /// </summary>
    public IDisposable Use()
    {
        var before = new Before(CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        var culture = Current.Culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        return before;
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    private async Task SaveAsync(string code, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var installation = await db.Installations.SingleOrDefaultAsync(cancellationToken);
        if (installation is null)
        {
            installation = new Installation();
            db.Installations.Add(installation);
        }

        installation.Language = code;
        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed class Before : IDisposable
    {
        private readonly CultureInfo _culture;
        private readonly CultureInfo _uiCulture;

        public Before(CultureInfo culture, CultureInfo uiCulture)
        {
            _culture = culture;
            _uiCulture = uiCulture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _uiCulture;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The saved language {Saved} isn't in this version of the app; it runs in {Fallback}")]
    private partial void LogUnknown(string saved, string fallback);

    [LoggerMessage(Level = LogLevel.Information, Message = "The app's language is now {Language}")]
    private partial void LogChosen(string language);
}
