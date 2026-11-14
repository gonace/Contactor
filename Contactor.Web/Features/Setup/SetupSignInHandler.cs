using Contactor.Web.Features.Access;
using Contactor.Web.Features.Cloudflare;
using Contactor.Web.Features.Identity;
using Contactor.Web.Features.Tailscale;

namespace Contactor.Web.Features.Setup;

/// <summary>Where Part 1 of the setup guide goes on.</summary>
public enum SetupPart1Step
{
    /// <summary>"How do people reach the app?": no way in is chosen yet.</summary>
    WayIn,

    /// <summary>"How do people sign in?": the way in is chosen, the sign-in isn't.</summary>
    SignIn,

    /// <summary>Part 1 is done: the guide goes on with "About you".</summary>
    Done,
}

/// <summary>A sign-in as the setup guide shows it for the chosen way in.</summary>
/// <param name="Method">The sign-in.</param>
/// <param name="Allowed">Whether it works with the way in; the guide shows the rest greyed out.</param>
/// <param name="Recommended">Whether it is the one that fits the way in best.</param>
/// <param name="Reason">Why it isn't allowed, e.g. "Only with Cloudflare", or null.</param>
public sealed record SetupSignInOption(SignInMethod Method, bool Allowed, bool Recommended, string? Reason);

/// <summary>
/// Part 1 of the setup guide: how people reach the app and how they sign in. Between the guide and the choices
/// (<see cref="AccessChoice"/>, <see cref="SignInChoice"/>) and Cloudflare's and Tailscale's settings.
/// </summary>
public sealed class SetupSignInHandler : IDisposable
{
    private readonly AccessChoice _access;
    private readonly SignInChoice _signIn;
    private readonly CloudflareSettings _cloudflare;
    private readonly CloudflareTunnel _tunnel;
    private readonly TailscaleSettings _tailscale;
    private readonly TailscaleNetwork _tailscaleNetwork;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SetupSignInHandler(
        AccessChoice access,
        SignInChoice signIn,
        CloudflareSettings cloudflare,
        CloudflareTunnel tunnel,
        TailscaleSettings tailscale,
        TailscaleNetwork tailscaleNetwork)
    {
        _access = access;
        _signIn = signIn;
        _cloudflare = cloudflare;
        _tunnel = tunnel;
        _tailscale = tailscale;
        _tailscaleNetwork = tailscaleNetwork;
    }

    /// <summary>Where Part 1 goes on: a new installation starts at the way in; one whose sign-in was reset at the sign-in.</summary>
    public SetupPart1Step Step => _access.Current is null || NeedsAddress ? SetupPart1Step.WayIn
        : _signIn.Current is null ? SetupPart1Step.SignIn
        : SetupPart1Step.Done;

    // Cloudflare without an address: an installation set up with .env, whose sign-in was reset. The way-in step asks
    // for it, since the guide sends people there.
    private bool NeedsAddress => _signIn.Current is null
        && _access.Current?.Id == CloudflareMethods.TunnelId
        && string.IsNullOrEmpty(_cloudflare.Tunnel.Address);

    /// <summary>The way in chosen, or null.</summary>
    public AccessMethod? WayIn => _access.Current;

    /// <summary>The tunnel's address and whether cloudflared is connected, as Cloudflare's step shows them.</summary>
    public (string? Address, bool Connected) Tunnel => (_cloudflare.Tunnel.Address, _tunnel.Connected);

    /// <summary>Cloudflare Access's team domain and AUD tag as they are now, to fill its fields.</summary>
    public CloudflareAccessSettings Access => _cloudflare.Access;

    /// <summary>The tunnel connected or went down.</summary>
    public event EventHandler? TunnelChanged
    {
        add => _tunnel.ConnectedChanged += value;
        remove => _tunnel.ConnectedChanged -= value;
    }

    /// <summary>The app's name on the Tailscale network as saved, to fill its field.</summary>
    public string TailscaleHostName => _tailscale.Current.HostName;

    /// <summary>How the app stands on the Tailscale network, as Tailscale's step shows it.</summary>
    public TailscaleStatus Tailscale => _tailscaleNetwork.Status;

    /// <summary>The app joined or left the Tailscale network, or something stops it.</summary>
    public event EventHandler? TailscaleChanged
    {
        add => _tailscaleNetwork.StatusChanged += value;
        remove => _tailscaleNetwork.StatusChanged -= value;
    }

    /// <summary>
    /// Every sign-in, for the chosen way in: those it works with first, the recommended one at the top, then the rest
    /// with why they can't be chosen. Nothing before a way in is chosen.
    /// </summary>
    public IReadOnlyList<SetupSignInOption> SignIns()
    {
        if (_access.Current is not { } wayIn)
        {
            return [];
        }

        var recommended = wayIn.SignIns.Select(SignInCatalogue.Find).FirstOrDefault(m => m is { NotRecommended: false });
        return [.. SignInCatalogue.All
            .Select(m => new SetupSignInOption(
                m,
                wayIn.SignIns.Contains(m.Id),
                m == recommended,
                wayIn.SignIns.Contains(m.Id) ? null : OnlyWith(m)))
            .OrderBy(o => o.Allowed ? (o.Recommended ? 0 : 1) : 2)];
    }

    /// <summary>
    /// Chooses how people reach the app; Cloudflare also needs its tunnel's token and address, and Tailscale an auth
    /// key and the app's name on the Tailscale network.
    /// </summary>
    public Task<string?> ChooseWayInAsync(string id, WayInFields fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return OneAtATimeAsync(() => AccessCatalogue.Find(id)?.Id switch
        {
            CloudflareMethods.TunnelId => ChooseCloudflareCoreAsync(fields.TunnelToken, fields.TunnelAddress, cancellationToken),
            TailscaleMethods.NetworkId => ChooseTailscaleCoreAsync(fields.AuthKey, fields.HostName, cancellationToken),
            AccessCatalogue.LocalId => ChooseLocalCoreAsync(cancellationToken),
            _ => Task.FromResult<string?>("Choose how people reach the app."),
        }, cancellationToken);
    }

    /// <summary>Chooses how people sign in; Cloudflare Access also needs its team domain and AUD tag.</summary>
    public Task<string?> ChooseSignInAsync(string id, string? teamDomain, string? audience, CancellationToken cancellationToken)
    {
        return OneAtATimeAsync(() => SignInCatalogue.Find(id)?.Id switch
        {
            CloudflareMethods.AccessId => ChooseCloudflareAccessCoreAsync(teamDomain, audience, cancellationToken),
            TailscaleMethods.IdentityId => ChooseTailscaleIdentityCoreAsync(cancellationToken),
            SignInCatalogue.NoneId => ChooseNoSignInCoreAsync(cancellationToken),
            _ => Task.FromResult<string?>("Choose how people sign in."),
        }, cancellationToken);
    }

    /// <summary>"Only on this network": nothing to save but the choice.</summary>
    private async Task<string?> ChooseLocalCoreAsync(CancellationToken cancellationToken)
    {
        if (_signIn.Current is not null)
        {
            return "This installation is set up already.";
        }

        await _access.ChooseAsync(AccessCatalogue.Find(AccessCatalogue.LocalId)!, cancellationToken);
        return null;
    }

    /// <summary>No sign-in: only with "Only on this network", never with a public link.</summary>
    private async Task<string?> ChooseNoSignInCoreAsync(CancellationToken cancellationToken)
    {
        if (_signIn.Current is not null)
        {
            return "This installation is set up already.";
        }

        if (_access.Current is not { } wayIn || !wayIn.SignIns.Contains(SignInCatalogue.NoneId))
        {
            return "No sign-in only works when the app is only on this network.";
        }

        await _signIn.ChooseAsync(SignInCatalogue.Find(SignInCatalogue.NoneId)!, cancellationToken);
        return null;
    }

    /// <summary>"Only on this network": nothing to save but the choice.</summary>
    public Task<string?> ChooseLocalAsync(CancellationToken cancellationToken) => OneAtATimeAsync(() => ChooseLocalCoreAsync(cancellationToken), cancellationToken);

    /// <summary>No sign-in: only with "Only on this network", never with a public link.</summary>
    public Task<string?> ChooseNoSignInAsync(CancellationToken cancellationToken) => OneAtATimeAsync(() => ChooseNoSignInCoreAsync(cancellationToken), cancellationToken);

    /// <summary>Cloudflare as the way in, with its tunnel's token and address.</summary>
    public Task<string?> ChooseCloudflareAsync(string? token, string? address, CancellationToken cancellationToken) =>
        OneAtATimeAsync(() => ChooseCloudflareCoreAsync(token, address, cancellationToken), cancellationToken);

    /// <summary>Tailscale as the way in, with its auth key and the app's name on the Tailscale network.</summary>
    public Task<string?> ChooseTailscaleAsync(string? authKey, string? hostName, CancellationToken cancellationToken) =>
        OneAtATimeAsync(() => ChooseTailscaleCoreAsync(authKey, hostName, cancellationToken), cancellationToken);

    /// <summary>Cloudflare Access as the sign-in, with its team domain and AUD tag.</summary>
    public Task<string?> ChooseCloudflareAccessAsync(string? teamDomain, string? audience, CancellationToken cancellationToken) =>
        OneAtATimeAsync(() => ChooseCloudflareAccessCoreAsync(teamDomain, audience, cancellationToken), cancellationToken);

    public void Dispose()
    {
        _gate.Dispose();
    }

    // Every choice checks the other one, then saves its own: one at a time (two tabs of the guide), so a check can't
    // pass on a choice another tab is changing, e.g. Cloudflare saved while No sign-in is.
    private async Task<string?> OneAtATimeAsync(Func<Task<string?>> choose, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await choose();
        }
        finally
        {
            _gate.Release();
        }
    }

    // "Only with Cloudflare", or "Only on this network.": the ways in a sign-in works with.
    private static string OnlyWith(SignInMethod method)
    {
        var ways = AccessCatalogue.All.Where(w => w.SignIns.Contains(method.Id)).Select(w => w.Name).ToList();
        return ways switch
        {
            [] => "Not available yet.",
            [var only] when only.StartsWith("Only ", StringComparison.Ordinal) => $"{only}.",
            _ => $"Only with {string.Join(" or ", ways)}.",
        };
    }

    /// <summary>The sign-in chosen, or null.</summary>
    public SignInMethod? SignIn => _signIn.Current;

    /// <summary>
    /// Cloudflare as the way in: saves the tunnel's token and address, and starts the tunnel. An empty token keeps the
    /// one saved before, once Cloudflare is the way in (the guide never shows it again). Returns what is wrong in plain
    /// words, and changes nothing then.
    /// </summary>
    private async Task<string?> ChooseCloudflareCoreAsync(string? token, string? address, CancellationToken cancellationToken)
    {
        if (_signIn.Current is not null)
        {
            return "This installation is set up already.";
        }

        if (string.IsNullOrWhiteSpace(token) && _access.Current?.Id == CloudflareMethods.TunnelId)
        {
            token = _cloudflare.Tunnel.Token;
        }

        var problem = await _cloudflare.SaveTunnelAsync(token, address, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        await _access.ChooseAsync(AccessCatalogue.Find(CloudflareMethods.TunnelId)!, cancellationToken);
        return null;
    }

    /// <summary>
    /// Tailscale as the way in: saves the auth key and the app's name, and starts Tailscale. An empty key keeps the one
    /// saved before, once Tailscale is the way in (the guide never shows it again). Returns what is wrong in plain
    /// words, and changes nothing then.
    /// </summary>
    private async Task<string?> ChooseTailscaleCoreAsync(string? authKey, string? hostName, CancellationToken cancellationToken)
    {
        if (_signIn.Current is not null)
        {
            return "This installation is set up already.";
        }

        if (string.IsNullOrWhiteSpace(authKey) && _access.Current?.Id == TailscaleMethods.NetworkId)
        {
            authKey = _tailscale.Current.AuthKey;
        }

        var problem = await _tailscale.SaveAsync(authKey, hostName, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        await _access.ChooseAsync(AccessCatalogue.Find(TailscaleMethods.NetworkId)!, cancellationToken);
        return null;
    }

    /// <summary>
    /// Tailscale identity as the sign-in, from the next request on, which ends setup mode. Only once the app is on the
    /// Tailscale network, since the guide sends people to its address there. Returns what is wrong in plain words, and
    /// changes nothing then.
    /// </summary>
    private async Task<string?> ChooseTailscaleIdentityCoreAsync(CancellationToken cancellationToken)
    {
        if (_signIn.Current is not null)
        {
            return "This installation is set up already.";
        }

        if (_access.Current is not { } wayIn || !wayIn.SignIns.Contains(TailscaleMethods.IdentityId))
        {
            return "Tailscale identity only works when people reach the app through Tailscale.";
        }

        if (_tailscaleNetwork.Status is not { Online: true, Address: not null })
        {
            return "Wait until Contactor is on your Tailscale network: people open it there.";
        }

        await _signIn.ChooseAsync(SignInCatalogue.Find(TailscaleMethods.IdentityId)!, cancellationToken);
        return null;
    }

    /// <summary>Tailscale identity as the sign-in, once the app is on the Tailscale network.</summary>
    public Task<string?> ChooseTailscaleIdentityAsync(CancellationToken cancellationToken) =>
        OneAtATimeAsync(() => ChooseTailscaleIdentityCoreAsync(cancellationToken), cancellationToken);

    /// <summary>
    /// Cloudflare Access as the sign-in: saves its team domain and AUD tag, and uses it from the next request on, which
    /// ends setup mode. Returns what is wrong in plain words, and changes nothing then.
    /// </summary>
    private async Task<string?> ChooseCloudflareAccessCoreAsync(string? teamDomain, string? audience, CancellationToken cancellationToken)
    {
        if (_signIn.Current is not null)
        {
            return "This installation is set up already.";
        }

        if (_access.Current is not { } wayIn || !wayIn.SignIns.Contains(CloudflareMethods.AccessId))
        {
            return "Cloudflare Access only works when people reach the app through Cloudflare.";
        }

        if (NeedsAddress)
        {
            return "Go back and save the tunnel's address first: people open Contactor there.";
        }

        var problem = await _cloudflare.SaveAccessAsync(teamDomain, audience, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        await _signIn.ChooseAsync(SignInCatalogue.Find(CloudflareMethods.AccessId)!, cancellationToken);
        return null;
    }
}

/// <summary>What the way-in step asks for: each way in uses its own, and ignores the rest.</summary>
/// <param name="TunnelToken">Cloudflare: the tunnel's token.</param>
/// <param name="TunnelAddress">Cloudflare: the tunnel's public hostname.</param>
/// <param name="AuthKey">Tailscale: the auth key.</param>
/// <param name="HostName">Tailscale: the app's name on the Tailscale network.</param>
public sealed record WayInFields(string? TunnelToken = null, string? TunnelAddress = null, string? AuthKey = null, string? HostName = null);
