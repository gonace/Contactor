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

    /// <summary>The issuer Access puts in its tokens.</summary>
    public string Issuer => $"https://{TeamDomain}";

    /// <summary>Where Access publishes its signing keys.</summary>
    public Uri CertificatesUri => new($"https://{TeamDomain}/cdn-cgi/access/certs");
}

/// <summary>
/// Checks <see cref="CloudflareAccessOptions"/> at start, so a wrong setting stops the app instead of letting
/// people in unchecked.
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

        List<string> errors = [];
        if (!Uri.CheckHostName(options.TeamDomain).Equals(UriHostNameType.Dns))
        {
            errors.Add("CloudflareAccess:TeamDomain must be a host name, e.g. myteam.cloudflareaccess.com.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            errors.Add("CloudflareAccess:Audience must be the Access application's AUD tag.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
