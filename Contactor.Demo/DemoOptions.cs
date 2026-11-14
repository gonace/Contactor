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

    /// <summary>How long a copy may go unused (no request, no open connection) before it is stopped and deleted.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How long the app or the simulator may take to start listening before the copy is given up.</summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(60);
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

        if (options.IdleTimeout < TimeSpan.FromMinutes(1) || options.IdleTimeout > TimeSpan.FromDays(1))
        {
            errors.Add("Demo:IdleTimeout is from 00:01:00 to 1.00:00:00.");
        }

        if (options.StartTimeout < TimeSpan.FromSeconds(1) || options.StartTimeout > TimeSpan.FromMinutes(10))
        {
            errors.Add("Demo:StartTimeout is from 00:00:01 to 00:10:00.");
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
