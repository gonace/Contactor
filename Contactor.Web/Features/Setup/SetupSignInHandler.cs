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

/// <summary>A sign-in the setup guide offers for the chosen way in.</summary>
/// <param name="Method">The sign-in.</param>
/// <param name="Recommended">Whether it is the one that fits the way in best.</param>
public sealed record SetupSignInOption(SignInMethod Method, bool Recommended);

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

    public SetupSignInHandler(AccessChoice access, SignInChoice signIn, CloudflareSettings cloudflare, CloudflareTunnel tunnel)
    {
        _access = access;
        _signIn = signIn;
        _cloudflare = cloudflare;
        _tunnel = tunnel;
    }

    /// <summary>Where Part 1 goes on: a new installation starts at the way in; one whose sign-in was reset at the sign-in.</summary>
    public SetupPart1Step Step => _access.Current is null ? SetupPart1Step.WayIn
        : _signIn.Current is null ? SetupPart1Step.SignIn
        : SetupPart1Step.Done;

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

    /// <summary>The sign-ins the chosen way in works with, the recommended one first.</summary>
    public IReadOnlyList<SetupSignInOption> SignIns()
    {
        return _access.Current is not { } wayIn
            ? []
            : [.. wayIn.SignIns.Select((id, i) => (Method: SignInCatalogue.Find(id), Recommended: i == 0))
                .Where(o => o.Method is not null)
                .Select(o => new SetupSignInOption(o.Method!, o.Recommended))];
    }

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

        var problem = await _cloudflare.SaveAccessAsync(teamDomain, audience, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        await _signIn.ChooseAsync(SignInCatalogue.Find(CloudflareMethods.AccessId)!, cancellationToken);
        return null;
    }
}
