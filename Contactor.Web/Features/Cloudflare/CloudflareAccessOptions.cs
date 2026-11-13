using Contactor.Web.Features.Identity;
using Microsoft.Extensions.Options;

namespace Contactor.Web.Features.Cloudflare;

/// <summary>
/// The <c>CloudflareAccess</c> configuration section.
/// </summary>
public sealed class CloudflareAccessOptions
{
    public const string SectionName = "CloudflareAccess";

    /// <summary>The Access team domain, e.g. <c>myteam.cloudflareaccess.com</c>: a host name only.</summary>
    public string TeamDomain { get; set; } = "";

    /// <summary>The Access application's AUD tag.</summary>
    public string Audience { get; set; } = "";

    /// <summary>
    /// Development only: everyone is signed in as this e-mail, without Cloudflare. The app refuses to start in
    /// Production when it is set.
    /// </summary>
    public string? DevUserEmail { get; set; }

    public bool UsesDevUser => !string.IsNullOrWhiteSpace(DevUserEmail);
}

/// <summary>
/// Checks <see cref="CloudflareAccessOptions"/> at start: the development user must never sign anyone in outside
/// Development. The team domain and AUD tag are checked at start only while Cloudflare Access is the chosen sign-in
/// (<see cref="CloudflareSettings"/>): they may come from the setup guide instead.
/// </summary>
public sealed class CloudflareAccessOptionsValidator : IValidateOptions<CloudflareAccessOptions>
{
    private readonly IHostEnvironment _environment;

    public CloudflareAccessOptionsValidator(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, CloudflareAccessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.UsesDevUser)
        {
            if (!_environment.IsDevelopment())
            {
                return ValidateOptionsResult.Fail(
                    $"CloudflareAccess:DevUserEmail is set in {_environment.EnvironmentName}; it is for Development only.");
            }

            return IdentityRules.IsValidEmail(IdentityRules.NormalizeEmail(options.DevUserEmail!))
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail("CloudflareAccess:DevUserEmail is not an e-mail address.");
        }

        return ValidateOptionsResult.Success;
    }
}
