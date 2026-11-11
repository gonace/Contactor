using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Contactor.Web.Features.Health;
using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Identity;

/// <summary>
/// Lets a request through only with a valid Cloudflare Access token (or, in Development, as
/// <c>CloudflareAccess:DevUserEmail</c>), and puts the user on <see cref="HttpContext.User"/>. Blazor Server copies
/// that user into the circuit when it starts, which is where <see cref="CurrentUser"/> reads it. An unknown e-mail
/// becomes a new <see cref="User"/>.
/// </summary>
public sealed partial class CloudflareAccessMiddleware
{
    public const string AuthenticationType = "CloudflareAccess";

    private readonly RequestDelegate _next;
    private readonly CloudflareAccessOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly CloudflareAccessTokenValidator _validator;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly ILogger<CloudflareAccessMiddleware> _logger;

    public CloudflareAccessMiddleware(
        RequestDelegate next,
        IOptions<CloudflareAccessOptions> options,
        IHostEnvironment environment,
        CloudflareAccessTokenValidator validator,
        IDbContextFactory<ContactorDbContext> dbFactory,
        ILogger<CloudflareAccessMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _next = next;
        _options = options.Value;
        _environment = environment;
        _validator = validator;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (IsOpenPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var email = _options.UsesDevUser
            ? IdentityRules.NormalizeEmail(_options.DevUserEmail!)
            : await _validator.ValidateAsync(context.Request.Headers[CloudflareAccessTokenValidator.HeaderName], context.RequestAborted);
        if (email is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Access denied.", context.RequestAborted);
            return;
        }

        var user = await FindOrCreateUserAsync(email, context.RequestAborted);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Email, user.Email), new Claim(ClaimTypes.Name, user.DisplayName)],
            AuthenticationType));
        await _next(context);
    }

    // The health checks answer only this machine (see their endpoints), which has no Access token.
    private bool IsOpenPath(PathString path)
    {
        return path.StartsWithSegments(HealthEndpoints.Path)
            || (_environment.IsDevelopment() && (path.StartsWithSegments("/health") || path.StartsWithSegments("/alive")));
    }

    private async Task<User> FindOrCreateUserAsync(string email, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == email, cancellationToken);
        if (user is not null)
        {
            return user;
        }

        user = new User { Email = email, DisplayName = IdentityRules.DisplayNameFor(email) };
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            LogUserCreated(user.DisplayName);
            return user;
        }
        catch (DbUpdateException)
        {
            // Another request created the same user first.
            await using var check = await _dbFactory.CreateDbContextAsync(cancellationToken);
            return await check.Users.AsNoTracking().SingleAsync(u => u.Email == email, cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created user {DisplayName} on first sign-in")]
    private partial void LogUserCreated(string displayName);
}
