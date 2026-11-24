using System.Net;
using Contactor.Boards;
using Microsoft.Extensions.Options;

namespace Contactor.Web.Features.Boards;

/// <summary>
/// The <c>Boards</c> configuration section: the only board addresses this app may use. Only the public demo sets it
/// (<c>Boards__OnlyHost=127.0.0.1</c>, <c>Boards__OnlyPorts=10000-10020</c>), so a visitor's app reaches only the
/// boards in their own simulator (Issue #606). Both empty, as in every other installation: any address.
/// </summary>
public sealed class BoardAddressOptions
{
    public const string SectionName = "Boards";

    /// <summary>The only host a board may have: an IP address written out in full, e.g. <c>127.0.0.1</c>.</summary>
    public string OnlyHost { get; set; } = "";

    /// <summary>The only ports a board may have, <c>first-last</c> (<see cref="PortRange"/>).</summary>
    public string OnlyPorts { get; set; } = "";
}

/// <summary>Checks <see cref="BoardAddressOptions"/> at start: both set and readable, or neither.</summary>
public sealed class BoardAddressOptionsValidation : IValidateOptions<BoardAddressOptions>
{
    public ValidateOptionsResult Validate(string? name, BoardAddressOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var host = options.OnlyHost ?? "";
        var ports = options.OnlyPorts ?? "";
        if (host.Length == 0 && ports.Length == 0)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> errors = [];
        if (!BoardAddressLimit.IsFullAddress(host))
        {
            errors.Add("Boards:OnlyHost is an IP address written out in full, e.g. 127.0.0.1, and is set with Boards:OnlyPorts.");
        }

        if (!PortRange.TryParse(ports, out _))
        {
            errors.Add("Boards:OnlyPorts is a range of ports, e.g. 10000-10020, and is set with Boards:OnlyHost.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

/// <summary>
/// Which board addresses the app may save and connect to (<see cref="BoardAddressOptions"/>). Without the setting,
/// any. With it, only the one host, written exactly so (not a name, nor another way of writing it, that leads there
/// too), and a port in the range.
/// </summary>
public sealed class BoardAddressLimit
{
    private readonly string? _host;
    private readonly PortRange? _ports;

    public BoardAddressLimit(IOptions<BoardAddressOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var host = options.Value.OnlyHost ?? "";
        var ports = options.Value.OnlyPorts ?? "";
        if (host.Length == 0 && ports.Length == 0)
        {
            return;
        }

        // Validated at start; should it be read without that, a setting that can't be read allows nothing.
        _host = IsFullAddress(host) ? host : "";
        _ports = PortRange.TryParse(ports, out var range) ? range : new PortRange(0, -1);
    }

    private BoardAddressLimit()
    {
    }

    /// <summary>No limit: every installation but the demo.</summary>
    public static BoardAddressLimit None { get; } = new();

    /// <summary>Whether the setting is there.</summary>
    public bool IsSet => _host is not null;

    /// <summary>The only host, or null without the setting.</summary>
    public string? Host => _host;

    /// <summary>The only ports, or null without the setting.</summary>
    public PortRange? Ports => _ports;

    /// <summary>Whether a board may be at <paramref name="endpoint"/>.</summary>
    public bool Allows(BoardEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return _host is null
            || (_host.Length > 0 && string.Equals(endpoint.Host, _host, StringComparison.Ordinal) && _ports!.Contains(endpoint.Port));
    }

    /// <summary>
    /// The address to point a connection at: <paramref name="endpoint"/> when allowed, or without a host when not, so
    /// nothing connects (as for a board whose address isn't entered yet). An address saved before the setting, or
    /// outside it, is kept on its row but never used.
    /// </summary>
    public BoardEndpoint Connectable(BoardEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return !endpoint.HasHost || Allows(endpoint) ? endpoint : endpoint with { Host = "" };
    }

    /// <summary>An IP address written out in full: not shorthand such as <c>127.1</c>, nor a name.</summary>
    public static bool IsFullAddress(string? text)
    {
        return IPAddress.TryParse(text, out var address)
            && string.Equals(address.ToString(), text, StringComparison.OrdinalIgnoreCase);
    }
}
