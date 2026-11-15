namespace Contactor.Web.Features.Updates;

/// <summary>The <c>Updates</c> configuration section.</summary>
public sealed class UpdateOptions
{
    public const string SectionName = "Updates";

    /// <summary>
    /// The folder the app shares with the update helper on the host (install/contactor-updater.sh); empty for an
    /// <c>updates</c> folder next to the database, as the helper expects (<c>/data/updates</c>).
    /// </summary>
    public string Folder { get; set; } = "";

    /// <summary>
    /// GitHub's public list of the app's releases, read once a day without a login, for the newest version and what's
    /// new in it. Nothing about the installation is sent. Empty: none is read (the demo's copies of the app).
    /// </summary>
    public string ReleasesUrl { get; set; } = "https://api.github.com/repos/gonace/Contactor/releases?per_page=30";
}
