using Contactor.Boards;
using Contactor.Web.Features.Access;
using Contactor.Web.Features.Cloudflare;
using Contactor.Web.Features.Identity;
using Contactor.Web.Features.Languages;
using Contactor.Web.Features.Tailscale;
using Contactor.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Contactor.Web.Features.Setup;

/// <summary>
/// The setup guide's words that depend on numbers or on a way in or sign-in, in the app's language. The catalogues
/// (<see cref="AccessCatalogue"/>, <see cref="SignInCatalogue"/>) keep their English names for the logs.
/// </summary>
public sealed class SetupText
{
    private readonly IStringLocalizer<Texts> _texts;

    public SetupText(IStringLocalizer<Texts> texts)
    {
        _texts = texts;
    }

    /// <summary>
    /// What a model has, for the list of boards: "8 relays · 1 input", "4 relays · 4 inputs · 1 analog input", and
    /// "1 relay · inputs read from the device" for a model whose inputs differ by device
    /// (<see cref="BoardCapabilities.InputCountFromBoard"/>).
    /// </summary>
    public string Capabilities(BoardCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var parts = new List<string> { _texts.Plural("Setup.Capabilities.Relays", capabilities.RelayCount) };
        if (capabilities.InputCountFromBoard)
        {
            parts.Add(_texts["Setup.Capabilities.InputsFromBoard"]);
        }
        else if (capabilities.InputCount > 0)
        {
            parts.Add(_texts.Plural("Setup.Capabilities.Inputs", capabilities.InputCount));
        }

        if (capabilities.AnalogInputCount > 0)
        {
            parts.Add(_texts.Plural("Setup.Capabilities.Analog", capabilities.AnalogInputCount));
        }

        return string.Join(_texts["Setup.Capabilities.Separator"], parts);
    }

    /// <summary>How many relays are in use and hidden, for the summary: "6 in use, 2 hidden", "8 in use", "None in use".</summary>
    public string RelaysInUse(int inUse, int hidden)
    {
        string used = inUse == 0 ? _texts["Setup.InUse.None"] : _texts["Setup.InUse", inUse];
        return hidden == 0 ? used : _texts["Setup.InUse.Hidden", used, hidden];
    }

    /// <summary>A way in's name, e.g. "Only on this network".</summary>
    public string Name(AccessMethod way)
    {
        ArgumentNullException.ThrowIfNull(way);
        return way.Id switch
        {
            AccessCatalogue.LocalId => _texts["Setup.WayIn.Local.Name"],
            CloudflareMethods.TunnelId => _texts["Setup.WayIn.Cloudflare.Name"],
            TailscaleMethods.NetworkId => _texts["Setup.WayIn.Tailscale.Name"],
            _ => way.Name,
        };
    }

    /// <summary>What a way in is and needs.</summary>
    public string Description(AccessMethod way)
    {
        ArgumentNullException.ThrowIfNull(way);
        return way.Id switch
        {
            AccessCatalogue.LocalId => _texts["Setup.WayIn.Local.Description"],
            CloudflareMethods.TunnelId => _texts["Setup.WayIn.Cloudflare.Description"],
            TailscaleMethods.NetworkId => _texts["Setup.WayIn.Tailscale.Description"],
            _ => way.Description,
        };
    }

    /// <summary>The setup guide's heading for an access group.</summary>
    public string Name(AccessGroup group)
    {
        return group switch
        {
            AccessGroup.LocalNetwork => _texts["Setup.WayIn.Group.LocalNetwork"],
            AccessGroup.PrivateAccess => _texts["Setup.WayIn.Group.PrivateAccess"],
            AccessGroup.PublicAddress => _texts["Setup.WayIn.Group.PublicAddress"],
            _ => throw new ArgumentOutOfRangeException(nameof(group), group, "Unknown access group."),
        };
    }

    /// <summary>A sign-in's name, e.g. "No sign-in".</summary>
    public string Name(SignInMethod signIn)
    {
        ArgumentNullException.ThrowIfNull(signIn);
        return signIn.Id switch
        {
            CloudflareMethods.AccessId => _texts["Setup.SignIn.CloudflareAccess.Name"],
            TailscaleMethods.IdentityId => _texts["Setup.SignIn.TailscaleIdentity.Name"],
            SignInCatalogue.NoneId => _texts["Setup.SignIn.None.Name"],
            _ => signIn.Name,
        };
    }

    /// <summary>What a sign-in is.</summary>
    public string Description(SignInMethod signIn)
    {
        ArgumentNullException.ThrowIfNull(signIn);
        return signIn.Id switch
        {
            CloudflareMethods.AccessId => _texts["Setup.SignIn.CloudflareAccess.Description"],
            TailscaleMethods.IdentityId => _texts["Setup.SignIn.TailscaleIdentity.Description"],
            SignInCatalogue.NoneId => _texts["Setup.SignIn.None.Description"],
            _ => signIn.Description,
        };
    }

    /// <summary>"Only with Cloudflare.", or "Only on this network.": the ways in a sign-in works with.</summary>
    public string OnlyWith(SignInMethod signIn)
    {
        ArgumentNullException.ThrowIfNull(signIn);
        var ways = AccessCatalogue.All.Where(w => w.SignIns.Contains(signIn.Id)).ToList();
        return ways switch
        {
            [] => _texts["Setup.Only.None"],
            [{ Id: AccessCatalogue.LocalId }] => _texts["Setup.Only.This"],
            _ => _texts["Setup.Only.With", string.Join(_texts["Setup.Only.Or"], ways.Select(Name))],
        };
    }
}
