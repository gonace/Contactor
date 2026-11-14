namespace Contactor.Web.Persistence;

/// <summary>Tailscale's settings as saved in the app (the setup guide). One row.</summary>
public sealed class TailscaleSetting
{
    public const int SingletonId = 1;
    public const int AuthKeyMaxLength = 512;
    public const int HostNameMaxLength = 63;

    /// <summary>The name the app has on the Tailscale network before anything is saved.</summary>
    public const string DefaultHostName = "contactor";

    public int Id { get; set; } = SingletonId;

    /// <summary>
    /// The auth key the app joins the Tailscale network with. A secret: never logged or shown, and given to tailscale
    /// only in a file of its own, never on its command line. Only the first join needs it: after that, tailscaled's
    /// state in /data is enough. It is in the database, so in the backups too, as the tunnel token is.
    /// </summary>
    public string? AuthKey { get; set; }

    /// <summary>The app's name on the Tailscale network, e.g. <c>contactor</c> in <c>contactor.tail1234.ts.net</c>.</summary>
    public string HostName { get; set; } = DefaultHostName;
}
