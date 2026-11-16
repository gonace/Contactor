using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Contactor.Web.Features.Access;
using Contactor.Web.Features.Health;
using Contactor.Web.Features.Setup;
using Contactor.Web.Persistence;
using Contactor.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Contactor.Web.Features.Identity;

/// <summary>
/// Lets a request through only when the chosen sign-in (<see cref="SignInChoice"/>) says who made it, and puts the
/// user on <see cref="HttpContext.User"/>. Blazor Server copies that user into the circuit when it starts, which is
/// where <see cref="CurrentUser"/> reads it. An unknown e-mail becomes a new <see cref="User"/>. While no sign-in is
/// chosen, the installation is being set up: only what <see cref="SetupMode"/> lets through answers, with nobody
/// signed in. With "Only on this network", and in setup mode, a request from outside this network gets 403 first
/// (<see cref="ThisNetwork"/>).
/// </summary>
public sealed partial class SignInMiddleware
{
    public const string AuthenticationType = "Contactor";

    /// <summary>How long after a warning about a request from outside this network the next one waits.</summary>
    public static readonly TimeSpan OutsideWarningInterval = TimeSpan.FromMinutes(1);

    private readonly RequestDelegate _next;
    private readonly SignInChoice _choice;
    private readonly AccessChoice _access;
    private readonly IServiceProvider _services;
    private readonly SetupMode _setup;
    private readonly IHostEnvironment _environment;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly ILogger<SignInMiddleware> _logger;
    private readonly TimeProvider _time;

    // When the last warning about a request from outside this network was logged, in UTC ticks; 0 before the first.
    private long _lastOutsideWarningTicks;

    public SignInMiddleware(
        RequestDelegate next,
        SignInChoice choice,
        AccessChoice access,
        IServiceProvider services,
        SetupMode setup,
        IHostEnvironment environment,
        IDbContextFactory<ContactorDbContext> dbFactory,
        ILogger<SignInMiddleware> logger,
        TimeProvider time)
    {
        _next = next;
        _choice = choice;
        _access = access;
        _services = services;
        _setup = setup;
        _environment = environment;
        _dbFactory = dbFactory;
        _logger = logger;
        _time = time;
    }

    // A refusal's words, in the app's language: the request already has its culture (UseRequestLocalization runs first).
    private static IStringLocalizer<Texts> Texts(HttpContext context)
    {
        return context.RequestServices.GetRequiredService<IStringLocalizer<Texts>>();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (IsOpenPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var signIn = _choice.Current;
        if (ThisNetwork.Required(_access.Current, signInChosen: signIn is not null) && !ThisNetwork.Asked(context))
        {
            WarnAboutOutside(context);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(Texts(context)["Setup.Refused.OwnNetwork"], context.RequestAborted);
            return;
        }

        string? email;
        if (signIn is { } method)
        {
            email = await method.Create(_services).AuthenticateAsync(context);
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
            await context.Response.WriteAsync(Texts(context)["Setup.Refused.AccessDenied"], context.RequestAborted);
            return;
        }

        var user = await FindOrCreateUserAsync(email, context.RequestAborted);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Email, user.Email), new Claim(ClaimTypes.Name, user.DisplayName)],
            AuthenticationType));
        await _next(context);
    }

    // The health checks and reset-sign-in's reload answer only this machine (see their endpoints), which isn't signed in.
    private bool IsOpenPath(PathString path)
    {
        return path.StartsWithSegments(HealthEndpoints.Path)
            || path.Equals(SetupEndpoints.ReloadPath, StringComparison.OrdinalIgnoreCase)
            || (_environment.IsDevelopment() && (path.StartsWithSegments("/health") || path.StartsWithSegments("/alive")));
    }

    // At most once a minute, so a scan of the open port doesn't fill the log.
    private void WarnAboutOutside(HttpContext context)
    {
        var now = _time.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastOutsideWarningTicks);
        // A clock set back counts as a minute gone, so it never holds the warnings back for long.
        if ((last != 0 && now >= last && now - last < OutsideWarningInterval.Ticks)
            || Interlocked.CompareExchange(ref _lastOutsideWarningTicks, now, last) != last)
        {
            return;
        }

        LogRefusedOutside(context.Connection.RemoteIpAddress?.ToString());
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a request from outside this network ({RemoteAddress}). The app's port can be reached from the internet: close it in the firewall or router")]
    private partial void LogRefusedOutside(string? remoteAddress);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created user {DisplayName} on first sign-in")]
    private partial void LogUserCreated(string displayName);
}
