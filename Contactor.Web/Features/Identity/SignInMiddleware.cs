using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Contactor.Web.Features.Cloudflare;
using Contactor.Web.Features.Health;
using Contactor.Web.Features.Setup;
using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Identity;

/// <summary>
/// Lets a request through only when the chosen sign-in (<see cref="SignInChoice"/>) says who made it, and puts the
/// user on <see cref="HttpContext.User"/>. Blazor Server copies that user into the circuit when it starts, which is
/// where <see cref="CurrentUser"/> reads it. An unknown e-mail becomes a new <see cref="User"/>. While no sign-in is
/// chosen, the installation is being set up: only what <see cref="SetupMode"/> lets through answers, with nobody
/// signed in.
/// </summary>
public sealed partial class SignInMiddleware
{
    public const string AuthenticationType = "Contactor";

    private readonly RequestDelegate _next;
    private readonly SignInChoice _choice;
    private readonly IServiceProvider _services;
    private readonly SetupMode _setup;
    private readonly string? _developmentUser;
    private readonly IHostEnvironment _environment;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly ILogger<SignInMiddleware> _logger;

    public SignInMiddleware(
        RequestDelegate next,
        SignInChoice choice,
        IServiceProvider services,
        SetupMode setup,
        IOptions<CloudflareAccessOptions> access,
        IHostEnvironment environment,
        IDbContextFactory<ContactorDbContext> dbFactory,
        ILogger<SignInMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(access);
        _next = next;
        _choice = choice;
        _services = services;
        _setup = setup;
        _environment = environment;
        _dbFactory = dbFactory;
        _logger = logger;

        // Development only (the app refuses to start with it anywhere else): everyone is this person, during setup too.
        _developmentUser = access.Value.UsesDevUser ? IdentityRules.NormalizeEmail(access.Value.DevUserEmail!) : null;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (IsOpenPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        string? email;
        if (_choice.Current is { } method)
        {
            email = await method.Create(_services).AuthenticateAsync(context);
        }
        else if (_developmentUser is not null)
        {
            email = _developmentUser;
        }
        else
        {
            if (await _setup.LetThroughAsync(context))
            {
                await _next(context);
            }

            return;
        }

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
