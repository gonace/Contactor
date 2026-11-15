namespace Contactor.Web.Features.Updates;

/// <summary>The <c>Updates</c> configuration section.</summary>
public sealed class UpdateOptions
{
    public const string SectionName = "Updates";

    /// <summary>
    /// The folder the app shares with the update helper on the host (install/contactor-updater.sh); empty for an
    /// <c>updates</c> folder next to the database. The helper always looks in <c>/opt/contactor/data/updates</c>, which
    /// is the container's <c>/data/updates</c>: the default with the database where the image keeps it
    /// (<c>/data/contactor.db</c>). Set it only for tests, or with a database elsewhere and a folder mapped to match.
    /// </summary>
    public string Folder { get; set; } = "";

    /// <summary>
    /// GitHub's public list of the app's releases, read once a day without a login, for the newest version and what's
    /// new in it. Nothing about the installation is sent. Empty: none is read (the demo's copies of the app).
    /// </summary>
    public string ReleasesUrl { get; set; } = "https://api.github.com/repos/gonace/Contactor/releases?per_page=30";
}
