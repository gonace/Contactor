using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;

namespace Contactor.Demo;

/// <summary>The <c>Demo</c> configuration section: where the app and the simulator are, and the demo's limits.</summary>
public sealed class DemoOptions
{
    public const string SectionName = "Demo";

    /// <summary>
    /// The app to start for each visitor: <c>Contactor.Web.dll</c> in its build output (development) or its published
    /// folder (the container). Relative paths are from the demo's content root.
    /// </summary>
    public string AppPath { get; set; } = "";

    /// <summary>The simulator to start for each visitor: <c>Contactor.Simulator.dll</c>, found as <see cref="AppPath"/>.</summary>
    public string SimulatorPath { get; set; } = "";

    /// <summary>
    /// The environment the copies run in: <c>Production</c> for published folders (the container); <c>Development</c>
    /// for build output, where only the development environment serves the programs' files from their projects.
    /// </summary>
    public string CopyEnvironment { get; set; } = "Production";

    /// <summary>
    /// Where each visitor's copy keeps its data, in a folder of its own that is deleted when the copy stops. The demo
    /// owns this folder: copies left from a demo that didn't stop cleanly are deleted at start.
    /// </summary>
    public string DataRoot { get; set; } = "";

    /// <summary>How many visitors may have a copy at once. The next one gets "The demo is busy right now".</summary>
    public int MaxVisitors { get; set; } = 10;

    /// <summary>
    /// How long a visitor may go without a click or a key in either frame before the page asks whether they are still
    /// there.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long the page counts down before the copy is deleted, once <see cref="IdleTimeout"/> has passed. Any click or
    /// key stops the count.
    /// </summary>
    public TimeSpan IdleWarning { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a copy lasts at most, used or not, so no script that keeps clicking holds a place.</summary>
    public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromHours(2);

    /// <summary>How many boards a visitor may have in their simulator (<c>Simulator:MaxBoards</c>).</summary>
    public int MaxBoards { get; set; } = 20;

    /// <summary>How long the app or the simulator may take to start listening before the copy is given up.</summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The address the tunnel's requests reach the demo from: the forwarder's address on the demo's network, which
    /// passes on what the tunnel's connector says. Only a request from there may say that the visitor came in over
    /// HTTPS (<c>X-Forwarded-Proto</c>), so the visitor's cookie is marked Secure. Empty: nobody may.
    /// </summary>
    public string TunnelAddress { get; set; } = "";

    /// <summary>
    /// Reads <see cref="TunnelAddress"/> as an IP address written out in full. <c>IPAddress.TryParse</c> alone also takes
    /// shorthand such as <c>172.30.60</c> (172.30.0.60) or <c>2886745090</c>, which would trust another address than the
    /// one written.
    /// </summary>
    public static bool TryParseTunnelAddress(string? text, [NotNullWhen(true)] out IPAddress? address)
    {
        var trimmed = text?.Trim();
        if (IPAddress.TryParse(trimmed, out var parsed)
            && string.Equals(parsed.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
        {
            address = parsed;
            return true;
        }

        address = null;
        return false;
    }
}

/// <summary>Makes relative paths in <see cref="DemoOptions"/> absolute, from the demo's content root.</summary>
public sealed class DemoOptionsPaths : IPostConfigureOptions<DemoOptions>
{
    private readonly string _contentRoot;

    public DemoOptionsPaths(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _contentRoot = environment.ContentRootPath;
    }

    public void PostConfigure(string? name, DemoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AppPath = Absolute(options.AppPath);
        options.SimulatorPath = Absolute(options.SimulatorPath);
        options.DataRoot = Absolute(options.DataRoot);
    }

    private string Absolute(string path)
    {
        return string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path, _contentRoot);
    }
}

/// <summary>Checks <see cref="DemoOptions"/> at start, so a demo that can't run says why at once.</summary>
public sealed class DemoOptionsValidation : IValidateOptions<DemoOptions>
{
    public ValidateOptionsResult Validate(string? name, DemoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> errors = [];
        CheckProgram(errors, nameof(DemoOptions.AppPath), options.AppPath);
        CheckProgram(errors, nameof(DemoOptions.SimulatorPath), options.SimulatorPath);
        if (string.IsNullOrWhiteSpace(options.DataRoot))
        {
            errors.Add("Demo:DataRoot is required: the folder where each visitor's copy keeps its data.");
        }
        else if (Path.GetPathRoot(options.DataRoot) == options.DataRoot)
        {
            errors.Add("Demo:DataRoot can't be the root of a drive: the demo deletes what it left there.");
        }

        if (options.CopyEnvironment is not ("Production" or "Development"))
        {
            errors.Add("Demo:CopyEnvironment is Production or Development.");
        }

        if (options.MaxVisitors is < 1 or > 100)
        {
            errors.Add("Demo:MaxVisitors is a number from 1 to 100.");
        }

        if (options.IdleTimeout < TimeSpan.FromSeconds(10) || options.IdleTimeout > TimeSpan.FromDays(1))
        {
            errors.Add("Demo:IdleTimeout is from 00:00:10 to 1.00:00:00.");
        }

        if (options.IdleWarning < TimeSpan.FromSeconds(5) || options.IdleWarning > TimeSpan.FromHours(1))
        {
            errors.Add("Demo:IdleWarning is from 00:00:05 to 01:00:00.");
        }

        if (options.MaxLifetime < options.IdleTimeout + options.IdleWarning || options.MaxLifetime > TimeSpan.FromDays(1))
        {
            errors.Add("Demo:MaxLifetime is at least Demo:IdleTimeout and Demo:IdleWarning together, and at most 1.00:00:00.");
        }

        if (options.MaxBoards is < 1 or > 100)
        {
            errors.Add("Demo:MaxBoards is a number from 1 to 100.");
        }

        if (options.MaxVisitors is >= 1 and <= 100 && options.MaxBoards is >= 1 and <= 100
            && options.MaxVisitors > BoardPortBlocks.Room / (options.MaxBoards + 1))
        {
            errors.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Demo:MaxVisitors × (Demo:MaxBoards + 1) is at most {BoardPortBlocks.Room}: each visitor gets board ports of their own."));
        }

        if (options.StartTimeout < TimeSpan.FromSeconds(1) || options.StartTimeout > TimeSpan.FromMinutes(10))
        {
            errors.Add("Demo:StartTimeout is from 00:00:01 to 00:10:00.");
        }

        if (!string.IsNullOrWhiteSpace(options.TunnelAddress)
            && !DemoOptions.TryParseTunnelAddress(options.TunnelAddress, out _))
        {
            errors.Add("Demo:TunnelAddress is empty or an IP address, e.g. 172.30.60.2.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void CheckProgram(List<string> errors, string setting, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add($"Demo:{setting} is required: the path to the program's .dll.");
        }
        else if (!File.Exists(path))
        {
            errors.Add($"Demo:{setting}: there is no {path}. Build or publish it first.");
        }
    }
}
