using Contactor.Web.Features.Access;
using Contactor.Web.Features.Cloudflare;
using Contactor.Web.Features.Identity;

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
/// (<see cref="AccessChoice"/>, <see cref="SignInChoice"/>) and Cloudflare's settings.
/// </summary>
public sealed class SetupSignInHandler
{
    private readonly AccessChoice _access;
    private readonly SignInChoice _signIn;
    private readonly CloudflareSettings _cloudflare;
    private readonly CloudflareTunnel _tunnel;

    public SetupSignInHandler(
        AccessChoice access,
        SignInChoice signIn,
        CloudflareSettings cloudflare,
        CloudflareTunnel tunnel)
    {
        _access = access;
        _signIn = signIn;
        _cloudflare = cloudflare;
        _tunnel = tunnel;
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

    /// <summary>Chooses how people reach the app; Cloudflare also needs its tunnel's token and address.</summary>
    public Task<string?> ChooseWayInAsync(string id, string? token, string? address, CancellationToken cancellationToken)
    {
        return AccessCatalogue.Find(id)?.Id switch
        {
            CloudflareMethods.TunnelId => ChooseCloudflareAsync(token, address, cancellationToken),
            AccessCatalogue.LocalId => ChooseLocalAsync(cancellationToken),
            _ => Task.FromResult<string?>("Choose how people reach the app."),
        };
    }

    /// <summary>Chooses how people sign in; Cloudflare Access also needs its team domain and AUD tag.</summary>
    public Task<string?> ChooseSignInAsync(string id, string? teamDomain, string? audience, CancellationToken cancellationToken)
    {
        return SignInCatalogue.Find(id)?.Id switch
        {
            CloudflareMethods.AccessId => ChooseCloudflareAccessAsync(teamDomain, audience, cancellationToken),
            SignInCatalogue.NoneId => ChooseNoSignInAsync(cancellationToken),
            _ => Task.FromResult<string?>("Choose how people sign in."),
        };
    }

    /// <summary>"Only on this network": nothing to save but the choice.</summary>
    public async Task<string?> ChooseLocalAsync(CancellationToken cancellationToken)
    {
        if (_signIn.Current is not null)
        {
            return "This installation is set up already.";
        }

        await _access.ChooseAsync(AccessCatalogue.Find(AccessCatalogue.LocalId)!, cancellationToken);
        return null;
    }

    /// <summary>No sign-in: only with "Only on this network", never with a public link.</summary>
    public async Task<string?> ChooseNoSignInAsync(CancellationToken cancellationToken)
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
    public async Task<string?> ChooseCloudflareAsync(string? token, string? address, CancellationToken cancellationToken)
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
    /// Cloudflare Access as the sign-in: saves its team domain and AUD tag, and uses it from the next request on, which
    /// ends setup mode. Returns what is wrong in plain words, and changes nothing then.
    /// </summary>
    public async Task<string?> ChooseCloudflareAccessAsync(string? teamDomain, string? audience, CancellationToken cancellationToken)
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
