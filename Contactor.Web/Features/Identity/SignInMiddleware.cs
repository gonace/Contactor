using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Contactor.Web.Features.Health;
using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Identity;

/// <summary>
/// Lets a request through only when the chosen sign-in (<see cref="SignInChoice"/>) says who made it, and puts the
/// user on <see cref="HttpContext.User"/>. Blazor Server copies
/// that user into the circuit when it starts, which is where <see cref="CurrentUser"/> reads it. An unknown e-mail
/// becomes a new <see cref="User"/>.
/// </summary>
public sealed partial class SignInMiddleware
{
    public const string AuthenticationType = "Contactor";

    private readonly RequestDelegate _next;
    private readonly SignInChoice _choice;
    private readonly IServiceProvider _services;
    private readonly IHostEnvironment _environment;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly ILogger<SignInMiddleware> _logger;

    public SignInMiddleware(
        RequestDelegate next,
        SignInChoice choice,
        IServiceProvider services,
        IHostEnvironment environment,
        IDbContextFactory<ContactorDbContext> dbFactory,
        ILogger<SignInMiddleware> logger)
    {
        _next = next;
        _choice = choice;
        _services = services;
        _environment = environment;
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

        var email = await _choice.Current.Create(_services).AuthenticateAsync(context);
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

    // The health checks answer only this machine (see their endpoints), which isn't signed in.
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
